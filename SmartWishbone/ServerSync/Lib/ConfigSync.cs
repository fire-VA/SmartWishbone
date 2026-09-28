using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

#pragma warning disable CS0649

namespace ServerSync
{
    // Standalone copy of the ConfigSync in Fires Unified Core (FiresCore.Sync), which is a fork of
    // blaxxun-boop's ServerSync (https://github.com/blaxxun-boop/ServerSync, MIT-0), compiled into this
    // mod instead of the prebuilt ServerSync.dll. That DLL was built before Valheim 1.0 turned
    // ZRoutedRpc.Everybody into a constant, so its SettingChanged handler failed on every call and a
    // client never ran the mod's own handler for synced values. Types are internal so other mods'
    // reflection lookups of ServerSync types by name never bind to this copy. No version check.
    //
    // Server-locked BepInEx config sync. Fork of the community-canonical
    // ConfigSync pattern. Server is the source of truth; a joining client
    // receives every synced entry right after login, later changes arrive
    // via the routed "<Name> ConfigSync" RPC, and clients mutate synced
    // entries only when AdminSyncing has flagged them as admin (lockExempt)
    // or the config is unlocked. Leaving the server restores local values.
    //
    // Wire framing carries three orthogonal flag bits:
    //   PartialConfigs    — sender is only re-broadcasting a delta
    //   FragmentedConfig  — payload split across multiple ZPackages
    //   CompressedConfig  — payload deflate-compressed
    internal class ConfigSync
    {
        private const byte PartialConfigs = 1;
        private const byte FragmentedConfig = 2;
        private const byte CompressedConfig = 4;

        private const int CompressionThresholdBytes = 10_000;
        private const int FragmentChunkBytes = 250_000;
        private const int PeerSendQueueBackpressureBytes = 20_000;
        private const float PeerSendQueueTimeoutSeconds = 30f;
        private const int FragmentCacheLifetimeSeconds = 60;
        private const double ReceivedLogThrottleSeconds = 12.0;
        private const string InternalSection = "Internal";
        private const string ReservedServerVersionIdentifier = "serverversion";
        private const string ConfigSyncRpcSuffix = " ConfigSync";
        private const int ManifestKeyHashPrime = 397;

        public static bool ProcessingServerUpdate = false;
        public static bool isServer;
        public static bool lockExempt = false;
        public static HashSet<ConfigSync> configSyncs = new HashSet<ConfigSync>();

        public readonly string Name;
        public string DisplayName;
        public string CurrentVersion;
        public string MinimumRequiredVersion;
        public bool ModRequired;

        public HashSet<OwnConfigEntryBase> allConfigs = new HashSet<OwnConfigEntryBase>();
        public HashSet<CustomSyncedValueBase> allCustomValues = new HashSet<CustomSyncedValueBase>();

        public event Action<bool> SourceOfTruthChanged;
        public event Action lockedConfigChanged;

        private bool? forceConfigLocking;
        private bool isSourceOfTruth = true;
        private bool initialSyncDone;
        private OwnConfigEntryBase lockedConfig;
        private DateTime lastConfigLogTime = DateTime.MinValue;

        private readonly Dictionary<string, SortedDictionary<int, byte[]>> configValueCache =
            new Dictionary<string, SortedDictionary<int, byte[]>>();
        private readonly List<KeyValuePair<long, string>> cacheExpirations =
            new List<KeyValuePair<long, string>>();

        private static long packageCounter = 0;

        public ConfigSync(string name)
        {
            Name = name;
            DisplayName = name;
            configSyncs.Add(this);
        }

        public bool IsLocked
        {
            get => (forceConfigLocking
                    ?? (lockedConfig?.BaseConfig.BoxedValue is IConvertible value
                        && value.ToInt32(CultureInfo.InvariantCulture) != 0))
                && !lockExempt;
            set => forceConfigLocking = value;
        }

        // ADMIN GATE — fail CLOSED. lockExempt is the server's explicit verdict (AdminSyncing
        // pushes true/false to every peer from the normalized adminlist). isSourceOfTruth must
        // never grant admin: it DEFAULTS true and only means "no server config package received
        // yet (or ever)" — any mod without a synced package read as admin-for-EVERYONE, leaking
        // every admin UI (configure hover, admin book, context menus, territory brush, wayshrine
        // tools) to non-admin clients. A local host / dedicated server is always admin.
        public bool IsAdmin => lockExempt || ZNet.instance == null || ZNet.instance.IsServer();

        public bool IsSourceOfTruth
        {
            get => isSourceOfTruth;
            internal set
            {
                if (value == isSourceOfTruth) return;
                isSourceOfTruth = value;
                SourceOfTruthChanged?.Invoke(value);
            }
        }

        public bool InitialSyncDone
        {
            get => initialSyncDone;
            internal set => initialSyncDone = value;
        }

        public void RequestSync()
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;

            var peers = (List<ZNetPeer>)AccessTools.Field(typeof(ZRoutedRpc), "m_peers")
                .GetValue(ZRoutedRpc.instance);
            var serverPeer = peers.FirstOrDefault(p => p.m_server);
            if (serverPeer == null) return;

            ZRoutedRpc.instance.InvokeRoutedRPC(serverPeer.m_uid, Name + ConfigSyncRpcSuffix, new ZPackage());
        }

        internal void SendAllValuesToJoiningPeer(ZNetPeer peer)
        {
            var joiningPeer = new List<ZNetPeer> { peer };
            ZNet.instance.StartCoroutine(SendZPackage(joiningPeer, BuildAllValuesPackage(), waitForSendQueue: false));
        }

        internal void RestoreLocalConfigs()
        {
            resetConfigsFromServer();
            IsSourceOfTruth = true;
            InitialSyncDone = false;
        }

        internal static void RefreshReadOnlyFlagsForAll()
        {
            foreach (var configSync in configSyncs)
                configSync.serverLockedSettingChanged();
        }

        private ZPackage BuildAllValuesPackage() =>
            ConfigsToPackage(allConfigs.Select(config => config.BaseConfig), allCustomValues, partial: false);

        private void SendAllValuesToRequestingPeer(long sender)
        {
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return;

            var requestingPeer = new List<ZNetPeer> { peer };
            ZNet.instance.StartCoroutine(SendZPackage(requestingPeer, BuildAllValuesPackage()));
        }

        public SyncedConfigEntry<T> AddConfigEntry<T>(ConfigEntry<T> configEntry)
        {
            var syncedEntry = configData(configEntry) as SyncedConfigEntry<T>
                ?? new SyncedConfigEntry<T>(configEntry);

            var tags = configEntry.Description.Tags ?? Array.Empty<object>();
            if (!tags.OfType<ConfigurationManagerAttributes>().Any())
                tags = new object[] { new ConfigurationManagerAttributes() }.Concat(tags).ToArray();
            tags = tags.Concat(new object[] { syncedEntry }).ToArray();

            AccessTools.Field(typeof(ConfigDescription), "<Tags>k__BackingField")
                .SetValue(configEntry.Description, tags);

            configEntry.SettingChanged += (sender, args) =>
            {
                if (ProcessingServerUpdate || !syncedEntry.SynchronizedConfig) return;
                Broadcast(ZRoutedRpc.Everybody, configEntry);
            };

            allConfigs.Add(syncedEntry);
            return syncedEntry;
        }

        public SyncedConfigEntry<T> AddLockingConfigEntry<T>(ConfigEntry<T> lockingConfig) where T : IConvertible
        {
            if (lockedConfig != null) throw new Exception("Cannot initialize locking ConfigEntry twice");

            lockedConfig = AddConfigEntry(lockingConfig);
            lockingConfig.SettingChanged += (sender, args) => lockedConfigChanged?.Invoke();

            return (SyncedConfigEntry<T>)lockedConfig;
        }

        internal void AddCustomValue(CustomSyncedValueBase customValue)
        {
            if (allCustomValues.Any(v => v.Identifier == customValue.Identifier)
                || customValue.Identifier == ReservedServerVersionIdentifier)
            {
                throw new Exception("Cannot have multiple settings with the same name or with a reserved name (serverversion)");
            }

            allCustomValues.Add(customValue);
            allCustomValues = new HashSet<CustomSyncedValueBase>(allCustomValues.OrderByDescending(v => v.Priority));

            customValue.ValueChanged += () =>
            {
                if (ProcessingServerUpdate) return;
                Broadcast(ZRoutedRpc.Everybody, customValue);
            };
        }

        public void Broadcast(long target, ConfigEntryBase config)
        {
            if (IsLocked && !IsAdmin) return;

            ZPackage package = ConfigsToPackage(new[] { config });
            ZNet.instance?.StartCoroutine(SendZPackage(target, package));
        }

        public void Broadcast(long target, CustomSyncedValueBase customValue)
        {
            if (IsLocked && !IsAdmin) return;

            ZPackage package = ConfigsToPackage(customValues: new[] { customValue });
            ZNet.instance?.StartCoroutine(SendZPackage(target, package));
        }

        internal void RPC_FromServerConfigSync(ZRpc rpc, ZPackage package)
        {
            lockedConfigChanged += serverLockedSettingChanged;
            IsSourceOfTruth = false;

            if (HandleConfigSyncRPC(0L, package, false))
                InitialSyncDone = true;
        }

        internal void RPC_FromOtherClientConfigSync(long sender, ZPackage package)
        {
            if (isServer && package.Size() == 0)
            {
                SendAllValuesToRequestingPeer(sender);
                return;
            }

            HandleConfigSyncRPC(sender, package, true);
        }

        private bool HandleConfigSyncRPC(long sender, ZPackage package, bool clientUpdate)
        {
            try
            {
                if (!IsCallerAllowedWhenLocked()) return false;

                ExpireStaleFragmentCacheEntries();

                byte flags = package.ReadByte();
                if (!TryReassembleFragments(sender, ref package, ref flags)) return false;

                ProcessingServerUpdate = true;

                if ((flags & CompressedConfig) != 0)
                    DecompressPackage(ref package, out flags);

                if ((flags & PartialConfigs) == 0)
                    resetConfigsFromServer();

                var parsed = ReadConfigsFromPackage(package);
                ApplyParsedConfigsAndCustomValues(parsed);
                MaybeLogReceived(parsed, sender, clientUpdate);

                if (!isServer) serverLockedSettingChanged();
                return true;
            }
            finally
            {
                ProcessingServerUpdate = false;
            }
        }

        private bool IsCallerAllowedWhenLocked()
        {
            if (!isServer || !IsLocked) return true;

            string hostName = SnatchCurrentlyHandlingRPC.currentRpc?.GetSocket()?.GetHostName();
            return hostName == null || AdminSyncing.AdminListContains(hostName);
        }

        private void ExpireStaleFragmentCacheEntries()
        {
            cacheExpirations.RemoveAll(kv =>
                kv.Key < DateTimeOffset.Now.Ticks && configValueCache.Remove(kv.Value));
        }

        private bool TryReassembleFragments(long sender, ref ZPackage package, ref byte flags)
        {
            if ((flags & FragmentedConfig) == 0) return true;

            long packageId = package.ReadLong();
            string cacheKey = sender.ToString() + packageId.ToString();

            if (!configValueCache.TryGetValue(cacheKey, out var fragments))
            {
                fragments = new SortedDictionary<int, byte[]>();
                configValueCache[cacheKey] = fragments;
                cacheExpirations.Add(new KeyValuePair<long, string>(
                    DateTimeOffset.Now.Ticks + FragmentCacheLifetimeSeconds * TimeSpan.TicksPerSecond,
                    cacheKey));
            }

            int fragmentIndex = package.ReadInt();
            int fragmentCount = package.ReadInt();
            fragments[fragmentIndex] = package.ReadByteArray();

            if (fragments.Count < fragmentCount) return false;

            configValueCache.Remove(cacheKey);
            package = new ZPackage(fragments.Values.SelectMany(a => a).ToArray());
            flags = package.ReadByte();
            return true;
        }

        private static void DecompressPackage(ref ZPackage package, out byte flags)
        {
            using (var input = new MemoryStream(package.ReadByteArray()))
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                    deflate.CopyTo(output);

                package = new ZPackage(output.ToArray());
            }
            flags = package.ReadByte();
        }

        // Holds SaveOnConfigSet=false across the batch so one Save() writes every
        // change at once, and skips that write when nothing changed (a file watcher
        // cannot tell a no-op rewrite from an admin's edit).
        private void ApplyParsedConfigsAndCustomValues(ParsedConfigs parsed)
        {
            ConfigFile configFile = null;
            bool saveOnSet = false;
            bool anyValueChanged = false;

            foreach (var kv in parsed.configValues)
            {
                if (!isServer && kv.Key.LocalBaseValue == null)
                    kv.Key.LocalBaseValue = kv.Key.BaseConfig.BoxedValue;

                if (configFile == null)
                {
                    configFile = kv.Key.BaseConfig.ConfigFile;
                    saveOnSet = configFile.SaveOnConfigSet;
                    configFile.SaveOnConfigSet = false;
                }

                object previousValue = kv.Key.BaseConfig.BoxedValue;
                kv.Key.BaseConfig.BoxedValue = kv.Value;
                anyValueChanged |= !Equals(previousValue, kv.Key.BaseConfig.BoxedValue);
            }

            if (configFile != null)
            {
                configFile.SaveOnConfigSet = saveOnSet;
                if (anyValueChanged) configFile.Save();
            }

            foreach (var kv in parsed.customValues)
            {
                if (!isServer && !kv.Key.HasLocalBaseValue)
                    kv.Key.LocalBaseValue = kv.Key.BoxedValue;
                kv.Key.BoxedValue = kv.Value;
            }
        }

        private void MaybeLogReceived(ParsedConfigs parsed, long sender, bool clientUpdate)
        {
            if ((DateTime.Now - lastConfigLogTime).TotalSeconds <= ReceivedLogThrottleSeconds) return;

            string origin = isServer || clientUpdate ? $"client {sender}" : "server";
            Debug.Log(
                $"Received {parsed.configValues.Count} configs and {parsed.customValues.Count} " +
                $"custom values from {origin} for mod {DisplayName ?? Name}");
            lastConfigLogTime = DateTime.Now;
        }

        private void serverLockedSettingChanged()
        {
            foreach (var config in allConfigs)
            {
                var attr = config.BaseConfig.Description.Tags?
                    .OfType<ConfigurationManagerAttributes>()
                    .FirstOrDefault();
                if (attr != null) attr.ReadOnly = !isWritableConfig(config);
            }
        }

        internal static bool isWritableConfig(OwnConfigEntryBase config)
        {
            var sync = configSyncs.FirstOrDefault(cs => cs.allConfigs.Contains(config));
            if (sync == null || sync.IsSourceOfTruth || !config.SynchronizedConfig || config.LocalBaseValue == null)
                return true;

            if (sync.IsLocked) return false;
            return config != sync.lockedConfig || lockExempt;
        }

        internal void resetConfigsFromServer()
        {
            ConfigFile configFile = null;
            bool saveOnSet = false;
            bool anyValueChanged = false;

            foreach (var config in allConfigs.Where(c => c.LocalBaseValue != null))
            {
                if (configFile == null)
                {
                    configFile = config.BaseConfig.ConfigFile;
                    saveOnSet = configFile.SaveOnConfigSet;
                    configFile.SaveOnConfigSet = false;
                }

                object previousValue = config.BaseConfig.BoxedValue;
                config.BaseConfig.BoxedValue = config.LocalBaseValue;
                anyValueChanged |= !Equals(previousValue, config.BaseConfig.BoxedValue);
                config.LocalBaseValue = null;
            }

            if (configFile != null)
            {
                configFile.SaveOnConfigSet = saveOnSet;
                if (anyValueChanged) configFile.Save();
            }

            foreach (var customValue in allCustomValues.Where(c => c.HasLocalBaseValue))
            {
                customValue.BoxedValue = customValue.LocalBaseValue;
                customValue.ClearLocalBaseValue();
            }

            lockedConfigChanged -= serverLockedSettingChanged;
            serverLockedSettingChanged();
        }

        private ParsedConfigs ReadConfigsFromPackage(ZPackage package)
        {
            var parsed = new ParsedConfigs();
            var configs = allConfigs.ToDictionary(
                c => $"{c.BaseConfig.Definition.Section}*{c.BaseConfig.Definition.Key}",
                c => c);

            int count = package.ReadInt();
            for (int i = 0; i < count; i++)
            {
                string section = package.ReadString();
                string key = package.ReadString();
                string typeName = package.ReadString();

                var type = Type.GetType(typeName);
                if (typeName != "" && type == null)
                {
                    Debug.LogWarning($"Got invalid type {typeName}, abort reading of received configs");
                    return new ParsedConfigs();
                }

                object value;
                try
                {
                    value = typeName == "" ? null : ReadValueWithTypeFromZPackage(package, type);
                }
                catch (InvalidDeserializationTypeException ex)
                {
                    Debug.LogWarning(
                        $"Got unexpected struct internal type {ex.received} for field {ex.field} " +
                        $"struct {typeName} for {key} in section {section} for mod {DisplayName ?? Name}, " +
                        $"expecting {ex.expected}");
                    continue;
                }

                if (section == InternalSection)
                    HandleInternalEntry(parsed, key, typeName, value);
                else if (configs.TryGetValue($"{section}*{key}", out var config))
                    HandleConfigEntry(parsed, config, section, key, typeName, value);
                else
                    Debug.LogWarning($"Received unknown config entry {key} in section {section} for mod {DisplayName ?? Name}.");
            }

            return parsed;
        }

        private void HandleInternalEntry(ParsedConfigs parsed, string key, string typeName, object value)
        {
            var customValue = allCustomValues.FirstOrDefault(v => v.Identifier == key);
            if (customValue == null) return;

            bool typeOk = typeName == ""
                && (!customValue.Type.IsValueType || Nullable.GetUnderlyingType(customValue.Type) != null);
            typeOk |= GetZPackageTypeString(CustomValueWireType(customValue.Type)) == typeName;

            if (typeOk)
                parsed.customValues[customValue] = FromCustomValueWireValue(customValue.Type, value);
            else
                Debug.LogWarning(
                    $"Got unexpected type {typeName} for internal value {key} for mod {DisplayName ?? Name}, " +
                    $"expecting {customValue.Type.AssemblyQualifiedName}");
        }

        private void HandleConfigEntry(ParsedConfigs parsed, OwnConfigEntryBase config,
                                        string section, string key, string typeName, object value)
        {
            var cfgType = configType(config.BaseConfig);
            bool typeOk = typeName == ""
                && (!cfgType.IsValueType || Nullable.GetUnderlyingType(cfgType) != null);
            typeOk |= GetZPackageTypeString(cfgType) == typeName;

            if (typeOk)
                parsed.configValues[config] = value;
            else
                Debug.LogWarning(
                    $"Got unexpected type {typeName} for {key} in section {section} for mod {DisplayName ?? Name}, " +
                    $"expecting {cfgType.AssemblyQualifiedName}");
        }

        private static string GetZPackageTypeString(Type type) => type.AssemblyQualifiedName;

        private static void AddValueToZPackage(ZPackage package, object value)
        {
            var type = value?.GetType();

            if (value is Enum)
                value = ((IConvertible)value).ToType(Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
            else if (value is ICollection collection)
            {
                WriteCollectionToPackage(package, collection);
                return;
            }
            else if (type != null && type.IsValueType && !type.IsPrimitive)
            {
                WriteStructFieldsToPackage(package, value, type);
                return;
            }

            ZRpc.Serialize(new object[] { value }, ref package);
        }

        private static void WriteCollectionToPackage(ZPackage package, ICollection collection)
        {
            package.Write(collection.Count);
            var enumerator = collection.GetEnumerator();
            try
            {
                while (enumerator.MoveNext())
                    AddValueToZPackage(package, enumerator.Current);
            }
            finally
            {
                if (enumerator is IDisposable disposable) disposable.Dispose();
            }
        }

        private static void WriteStructFieldsToPackage(ZPackage package, object value, Type type)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            package.Write(fields.Length);
            foreach (var field in fields)
            {
                package.Write(GetZPackageTypeString(field.FieldType));
                AddValueToZPackage(package, field.GetValue(value));
            }
        }

        private static object ReadValueWithTypeFromZPackage(ZPackage package, Type type)
        {
            if (type != null && type.IsValueType && !type.IsPrimitive && !type.IsEnum)
                return ReadStructFromPackage(package, type);

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                return ReadDictionaryFromPackage(package, type);

            if (type.IsGenericType)
            {
                var collectionType = typeof(ICollection<>).MakeGenericType(type.GenericTypeArguments[0]);
                if (collectionType.IsAssignableFrom(type))
                    return ReadCollectionFromPackage(package, type, collectionType);
            }

            return ReadScalarFromPackage(package, type);
        }

        private static object ReadStructFromPackage(ZPackage package, Type type)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int fieldCount = package.ReadInt();
            if (fieldCount != fields.Length)
            {
                throw new InvalidDeserializationTypeException
                {
                    received = $"(field count: {fieldCount})",
                    expected = $"(field count: {fields.Length})",
                };
            }

            object instance = FormatterServices.GetUninitializedObject(type);
            foreach (var field in fields)
            {
                string fieldTypeName = package.ReadString();
                if (fieldTypeName != GetZPackageTypeString(field.FieldType))
                {
                    throw new InvalidDeserializationTypeException
                    {
                        received = fieldTypeName,
                        expected = GetZPackageTypeString(field.FieldType),
                        field = field.Name,
                    };
                }
                field.SetValue(instance, ReadValueWithTypeFromZPackage(package, field.FieldType));
            }
            return instance;
        }

        private static object ReadDictionaryFromPackage(ZPackage package, Type dictType)
        {
            int count = package.ReadInt();
            var pairType = typeof(KeyValuePair<,>).MakeGenericType(dictType.GenericTypeArguments);
            var instance = (IDictionary)Activator.CreateInstance(dictType);

            var keyField = pairType.GetField("key", BindingFlags.Instance | BindingFlags.NonPublic);
            var valueField = pairType.GetField("value", BindingFlags.Instance | BindingFlags.NonPublic);

            for (int i = 0; i < count; i++)
            {
                var pair = ReadValueWithTypeFromZPackage(package, pairType);
                instance.Add(keyField.GetValue(pair), valueField.GetValue(pair));
            }
            return instance;
        }

        private static object ReadCollectionFromPackage(ZPackage package, Type type, Type collectionType)
        {
            int count = package.ReadInt();
            var instance = Activator.CreateInstance(type);
            var addMethod = collectionType.GetMethod("Add");
            for (int i = 0; i < count; i++)
                addMethod.Invoke(instance, new object[] { ReadValueWithTypeFromZPackage(package, type.GenericTypeArguments[0]) });
            return instance;
        }

        private static object ReadScalarFromPackage(ZPackage package, Type type)
        {
            var param = (ParameterInfo)FormatterServices.GetUninitializedObject(typeof(ParameterInfo));
            AccessTools.Field(typeof(ParameterInfo), "ClassImpl").SetValue(param, type);

            var parameters = new List<object>();
            ZRpc.Deserialize(new ParameterInfo[] { null, param }, package, ref parameters);
            return parameters.First();
        }

        internal ZPackage ConfigsToPackage(
            IEnumerable<ConfigEntryBase> configs = null,
            IEnumerable<CustomSyncedValueBase> customValues = null,
            IEnumerable<PackageEntry> packageEntries = null,
            bool partial = true)
        {
            var configList = configs?.Where(c => configData(c).SynchronizedConfig).ToList()
                ?? new List<ConfigEntryBase>();
            var customList = customValues?.ToList() ?? new List<CustomSyncedValueBase>();
            var entryList = packageEntries?.ToList() ?? new List<PackageEntry>();

            var package = new ZPackage();
            package.Write((byte)(partial ? PartialConfigs : 0));
            package.Write(configList.Count + customList.Count + entryList.Count);

            foreach (var entry in entryList)
            {
                package.Write(entry.section);
                package.Write(entry.key);
                package.Write(entry.value == null ? "" : GetZPackageTypeString(entry.type));
                AddValueToZPackage(package, entry.value);
            }

            foreach (var customValue in customList)
            {
                package.Write(InternalSection);
                package.Write(customValue.Identifier);
                package.Write(customValue.BoxedValue == null ? "" : GetZPackageTypeString(CustomValueWireType(customValue.Type)));
                AddValueToZPackage(package, customValue.BoxedValue);
            }

            foreach (var config in configList)
            {
                package.Write(config.Definition.Section);
                package.Write(config.Definition.Key);
                package.Write(GetZPackageTypeString(configType(config)));
                AddValueToZPackage(package, config.BoxedValue);
            }

            return package;
        }

        private static Type configType(ConfigEntryBase config) => configType(config.SettingType);

        private static Type configType(Type type) => type.IsEnum ? Enum.GetUnderlyingType(type) : type;

        // Vanilla ZRpc has no enum or Nullable reader, so those custom values travel as their underlying type.
        private static Type CustomValueWireType(Type type) => configType(Nullable.GetUnderlyingType(type) ?? type);

        private static object FromCustomValueWireValue(Type type, object wireValue)
        {
            Type valueType = Nullable.GetUnderlyingType(type) ?? type;
            return wireValue != null && valueType.IsEnum ? Enum.ToObject(valueType, wireValue) : wireValue;
        }

        public IEnumerator SendZPackage(long target, ZPackage package)
        {
            if (!ZNet.instance) yield break;

            var peers = ((List<ZNetPeer>)AccessTools.Field(typeof(ZRoutedRpc), "m_peers").GetValue(ZRoutedRpc.instance))
                .Where(p => target == ZRoutedRpc.Everybody || p.m_uid == target)
                .ToList();

            var enumerator = SendZPackage(peers, package);
            while (enumerator.MoveNext()) yield return enumerator.Current;
        }

        public IEnumerator SendZPackage(List<ZNetPeer> peers, ZPackage package) =>
            SendZPackage(peers, package, waitForSendQueue: true);

        private IEnumerator SendZPackage(List<ZNetPeer> peers, ZPackage package, bool waitForSendQueue)
        {
            if (!ZNet.instance) yield break;

            byte[] data = package.GetArray();
            if (data.Length > CompressionThresholdBytes)
                package = CompressPackage(data);

            var writers = peers
                .Where(p => p.IsReady())
                .Select(p => distributeConfigToPeers(p, package, waitForSendQueue))
                .ToList();

            writers.RemoveAll(w => !w.MoveNext());

            while (writers.Any())
            {
                yield return null;
                writers.RemoveAll(w => !w.MoveNext());
            }
        }

        private static ZPackage CompressPackage(byte[] data)
        {
            var compressed = new ZPackage();
            compressed.Write(CompressedConfig);

            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal))
                    deflate.Write(data, 0, data.Length);

                compressed.Write(output.ToArray());
            }
            return compressed;
        }

        private IEnumerator distributeConfigToPeers(ZNetPeer peer, ZPackage package, bool waitForSendQueue)
        {
            byte[] data = package.GetArray();

            if (data.Length > FragmentChunkBytes)
            {
                int fragments = (data.Length + (FragmentChunkBytes - 1)) / FragmentChunkBytes;
                long packageId = ++packageCounter;

                for (int i = 0; i < fragments; i++)
                {
                    if (waitForSendQueue)
                        foreach (bool wait in waitForQueue())
                            yield return wait;

                    if (!peer.m_socket.IsConnected()) break;

                    var pkg = new ZPackage();
                    pkg.Write(FragmentedConfig);
                    pkg.Write(packageId);
                    pkg.Write(i);
                    pkg.Write(fragments);
                    pkg.Write(data.Skip(FragmentChunkBytes * i).Take(FragmentChunkBytes).ToArray());

                    SendPackage(pkg);

                    if (i < fragments - 1) yield return true;
                }
            }
            else
            {
                if (waitForSendQueue)
                    foreach (bool wait in waitForQueue())
                        yield return wait;
                SendPackage(package);
            }

            // Literal "30 seconds" in the log message is the community-
            // canonical ConfigSync signature. TimeoutLimit (and other mods)
            // identify this disconnect site via an IL pattern match on the
            // literal string — promoting the value into the format string
            // breaks their transpilers in production.
            IEnumerable<bool> waitForQueue()
            {
                float timeout = Time.time + PeerSendQueueTimeoutSeconds;
                while (peer.m_socket.GetSendQueueSize() > PeerSendQueueBackpressureBytes)
                {
                    if (Time.time > timeout)
                    {
                        Debug.Log($"Disconnecting {peer.m_uid} after 30 seconds config sending timeout");
                        peer.m_rpc.Invoke("Error", ZNet.ConnectionStatus.ErrorConnectFailed);
                        ZNet.instance.Disconnect(peer);
                        break;
                    }
                    yield return false;
                }
            }

            void SendPackage(ZPackage pkg)
            {
                if (isServer)
                    peer.m_rpc.Invoke(Name + ConfigSyncRpcSuffix, pkg);
                else
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_server ? 0L : peer.m_uid, Name + ConfigSyncRpcSuffix, pkg);
            }
        }

        public static OwnConfigEntryBase configData(ConfigEntryBase config) =>
            config.Description.Tags?.OfType<OwnConfigEntryBase>().SingleOrDefault();

        private static T configAttribute<T>(ConfigEntryBase config) where T : class =>
            config.Description.Tags?.OfType<T>().FirstOrDefault();
    }

    internal abstract class OwnConfigEntryBase
    {
        public readonly ConfigEntryBase BaseConfig;
        public object LocalBaseValue;
        public bool SynchronizedConfig = true;

        protected OwnConfigEntryBase(ConfigEntryBase config) => BaseConfig = config;
    }

    internal class SyncedConfigEntry<T> : OwnConfigEntryBase
    {
        public T Value
        {
            get => BaseConfig is ConfigEntry<T> configEntry
                ? configEntry.Value
                : throw new InvalidOperationException($"Cannot cast BaseConfig to ConfigEntry<{typeof(T).Name}>");
            set => ((ConfigEntry<T>)BaseConfig).Value = value;
        }

        public T DefaultValue => (T)BaseConfig.BoxedValue;

        public SyncedConfigEntry(ConfigEntry<T> config) : base(config) { }
    }

    internal abstract class CustomSyncedValueBase
    {
        private readonly ConfigSync configSync;
        private object boxedValue;
        private object localBaseValue;

        public string Identifier { get; }
        public Type Type { get; }

        public object BoxedValue
        {
            get => boxedValue;
            set
            {
                if (Equals(boxedValue, value)) return;
                boxedValue = value;
                ValueChanged?.Invoke();
            }
        }

        public object LocalBaseValue
        {
            get => localBaseValue;
            set
            {
                localBaseValue = value;
                HasLocalBaseValue = true;
            }
        }

        public int Priority { get; }

        public event Action ValueChanged;

        internal bool HasLocalBaseValue { get; private set; }

        internal bool LocalIsOwner => configSync.IsSourceOfTruth;

        protected CustomSyncedValueBase(ConfigSync configSync, string identifier, Type type, int priority)
        {
            this.configSync = configSync;
            Identifier = identifier;
            Type = type;
            Priority = priority;
            configSync.AddCustomValue(this);
        }

        internal void ClearLocalBaseValue()
        {
            localBaseValue = null;
            HasLocalBaseValue = false;
        }
    }

    internal sealed class CustomSyncedValue<T> : CustomSyncedValueBase
    {
        public CustomSyncedValue(ConfigSync configSync, string identifier, T value = default, int priority = 0)
            : base(configSync, identifier, typeof(T), priority)
        {
            Value = value;
        }

        public T Value
        {
            get => (T)BoxedValue;
            set => BoxedValue = value;
        }

        public void AssignLocalValue(T value)
        {
            if (LocalIsOwner) Value = value;
            else LocalBaseValue = value;
        }
    }

    internal class ConfigurationManagerAttributes
    {
        public bool? ReadOnly;
        public Action<ConfigEntryBase> CustomDrawer;
    }

    internal class ParsedConfigs
    {
        public readonly Dictionary<OwnConfigEntryBase, object> configValues = new Dictionary<OwnConfigEntryBase, object>();
        public readonly Dictionary<CustomSyncedValueBase, object> customValues = new Dictionary<CustomSyncedValueBase, object>();
    }

    internal class PackageEntry
    {
        public string section;
        public string key;
        public Type type;
        public object value;
    }

    internal class InvalidDeserializationTypeException : Exception
    {
        public string expected;
        public string received;
        public string field = "";
    }

    [HarmonyPatch(typeof(ZRpc), "HandlePackage")]
    internal class SnatchCurrentlyHandlingRPC
    {
        public static ZRpc currentRpc;

        [HarmonyPrefix]
        private static void Prefix(ZRpc __instance) => currentRpc = __instance;
    }

    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal class RegisterRPCPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ZNet __instance)
        {
            ConfigSync.isServer = __instance.IsServer();

            foreach (var configSync in ConfigSync.configSyncs)
            {
                ZRoutedRpc.instance.Register<ZPackage>(
                    configSync.Name + " ConfigSync",
                    configSync.RPC_FromOtherClientConfigSync);

                if (ConfigSync.isServer)
                    Debug.Log($"Registered '{configSync.Name} ConfigSync' RPC - waiting for incoming connections");
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    internal class RegisterClientRPCPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer()) return;

            foreach (var configSync in ConfigSync.configSyncs)
                peer.m_rpc.Register<ZPackage>(
                    configSync.Name + " ConfigSync",
                    configSync.RPC_FromServerConfigSync);
        }
    }

    // Runs after vanilla accepted or rejected the login (only accepted peers have a uid) and after
    // other mods' postfixes, so their single-package config sends reach the socket before this one.
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    internal class SendServerConfigsToJoiningPeer
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ZNet __instance, ZRpc rpc)
        {
            if (!__instance.IsServer()) return;

            ZNetPeer peer = __instance.GetPeers().Find(candidate => candidate.m_rpc == rpc);
            if (peer == null || !peer.IsReady()) return;

            AdminSyncing.PushAdminStatus(new[] { peer });
            foreach (var configSync in ConfigSync.configSyncs)
                configSync.SendAllValuesToJoiningPeer(peer);
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnDestroy")]
    internal class RestoreLocalConfigsOnDisconnect
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            if (ConfigSync.isServer) return;

            ConfigSync.ProcessingServerUpdate = true;
            try
            {
                foreach (var configSync in ConfigSync.configSyncs)
                    configSync.RestoreLocalConfigs();
            }
            finally
            {
                ConfigSync.ProcessingServerUpdate = false;
            }
        }
    }

    [HarmonyPatch(typeof(ConfigEntryBase), "GetSerializedValue")]
    internal class PreventSavingServerInfo
    {
        [HarmonyPrefix]
        private static bool Prefix(ConfigEntryBase __instance, ref string __result)
        {
            var config = ConfigSync.configData(__instance);
            if (config == null || ConfigSync.isWritableConfig(config)) return true;

            __result = TomlTypeConverter.ConvertToString(config.LocalBaseValue, __instance.SettingType);
            return false;
        }
    }

    [HarmonyPatch(typeof(ConfigEntryBase), "SetSerializedValue")]
    internal class PreventConfigRereadChangingValues
    {
        [HarmonyPrefix]
        private static bool Prefix(ConfigEntryBase __instance, string value)
        {
            var config = ConfigSync.configData(__instance);
            if (config?.LocalBaseValue == null) return true;

            try
            {
                config.LocalBaseValue = TomlTypeConverter.ConvertToValue(value, __instance.SettingType);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"Config value of setting \"{__instance.Definition}\" could not be parsed and will be ignored. " +
                    $"Reason: {ex.Message}; Value: {value}");
            }
            return false;
        }
    }
}
