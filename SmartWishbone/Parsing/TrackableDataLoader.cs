using BepInEx;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SmartWishbone
{
    internal class TrackableDataLoader
    {
        private const string embeddedPathFormat = "SmartWishbone.Data.{0}.yaml";

        private const string dataFileInfix = "SmartWishbone.Data";

        private const string loadingLog = "Loading {0} data file '{1}'";
        private const string failedLoadLog = "Failed loading {0} data file '{1}'";
        private const string savingLog = "Saving {0} data file '{1}'";
        private const string failedSaveLog = "Failed saving {0} data file '{1}'";

        private const string external = "external";
        private const string embedded = "embedded";

        internal static Dictionary<string, Trackable> LoadTrackableData()
        {
            var dataList = LoadDataFiles();

            var dataDict = ValidateData(dataList);

            Helper.Log($"trackable count after validation: {dataDict.Count}");

            return dataDict;
        }

        // the search walks the whole BepInEx folder (142 ms on a modded client, 16 ms on a dedicated server), so the
        // file found at load is reused for every save until it disappears
        private static string externalDataFilePath;

        internal static void SaveTrackableData(string trackablesAsString)
        {
            if (externalDataFilePath == null || !File.Exists(externalDataFilePath))
            {
                externalDataFilePath = FindExternalDataFile();
            }

            if (externalDataFilePath == null)
            {
                Helper.LogWarning("No data file to save to found, aborting saving.");
            }
            else if (trackablesAsString == null)
            {
                Helper.LogWarning(string.Format(failedSaveLog, external, externalDataFilePath));
            }
            else
            {
                Helper.Log(string.Format(savingLog, external, externalDataFilePath));

                DataFileWriter.Enqueue(externalDataFilePath, trackablesAsString);
            }
        }

        private static string FindExternalDataFile()
        {
            var dataFilesFound = Directory.GetFiles(Path.GetDirectoryName(Paths.PluginPath), $"{dataFileInfix}.yaml", SearchOption.AllDirectories).ToList();

            if (dataFilesFound.Count == 0)
            {
                return null;
            }

            dataFilesFound.Sort();
            return dataFilesFound[0];
        }

        internal static List<Trackable> LoadDataFiles()
        {
            var dataFilesFound = Directory.GetFiles(Path.GetDirectoryName(Paths.PluginPath), $"{dataFileInfix}.yaml", SearchOption.AllDirectories).ToList();

            var trackables = new List<Trackable>();

            if (dataFilesFound.Count > 0)
            {
                dataFilesFound.Sort();
                var dataFilePath = dataFilesFound[0];
                externalDataFilePath = dataFilePath;

                if (dataFilesFound.Count > 1)
                {
                    Helper.LogWarning($"Found {dataFilesFound.Count} data files instead of only 1. Please remove the duplicate entries. Only reading entry: '{dataFilePath}'");
                }

                Helper.Log(string.Format(loadingLog, external, dataFilePath));
                var temp = LoadExternalDataFile(dataFilePath);

                if (temp != null && temp.Count > 0)
                {
                    trackables = temp;
                }
                else
                {
                    Helper.LogWarning(string.Format(failedLoadLog, external, dataFilePath));
                }
            }

            if (trackables == null || trackables.Count == 0)
            {
                Helper.Log(string.Format(loadingLog, embedded, dataFileInfix));
                var temp = LoadEmbeddedDataFile(dataFileInfix);

                if (temp != null && temp.Count > 0)
                {
                    trackables = temp;
                }
                else
                {
                    Helper.LogWarning(string.Format(failedLoadLog, embedded, dataFileInfix));
                }
            }

            if (trackables == null || trackables.Count == 0)
            {
                return new List<Trackable>();
            }
            else
            {
                return trackables;
            }
        }

        internal static Dictionary<string, Trackable> ValidateData(List<Trackable> trackables)
        {
            Dictionary<string, Trackable> trackableDict = new Dictionary<string, Trackable>();

            if (trackables == null)
            {
                return trackableDict;
            }

            foreach (var item in trackables)
            {
                if (trackableDict.ContainsKey(item.prefabName))
                {
                    Helper.LogWarning($"There were multiple definitions for the same prefab name: {item.prefabName}; only picking the first one.");
                }
                else
                {
                    trackableDict[item.prefabName] = item;
                }
            }

            foreach (var trackable in trackableDict.ToList())
            {
                if (trackable.Key == null || trackable.Key != trackable.Value.prefabName)
                {
                    Helper.LogWarning($"invalid trackable");
                    trackableDict.Remove(trackable.Key);
                    continue;
                }

                var trackableValue = trackable.Value;

                if (trackableValue.prefabName.IsNullOrWhiteSpace())
                {
                    Helper.LogWarning($"PrefabName is not a valid");
                    trackableDict.Remove(trackable.Key);
                    continue;
                }

                if (trackableValue.displayItem.IsNullOrWhiteSpace())
                {
                    Helper.LogWarning($"DisplayItem is not a valid");
                    trackableDict.Remove(trackable.Key);
                    continue;
                }

                if (trackableValue.condition.IsNullOrWhiteSpace())
                {
                    trackableValue.condition = null;
                }

                if (trackableValue.range <= 0)
                {
                    Helper.LogWarning($"Trackable range is not positive, resetting to default of 20");
                    trackableValue.range = 20;
                }
            }

            return trackableDict;
        }

        internal static List<Trackable> LoadExternalDataFile(string path)
        {
            string trackablesAsString = File.ReadAllText(path);

            if (trackablesAsString == null)
            {
                return null;
            }

            return ParseStringToCustomList(trackablesAsString);
        }

        internal static List<Trackable> LoadEmbeddedDataFile(string infix)
        {
            var path = string.Format(embeddedPathFormat, infix);

            string trackablesAsString = ReadEmbeddedTextFile(path);

            if (trackablesAsString == null || trackablesAsString.IndexOf('-') == -1)
            {
                return null;
            }

            // yaml really dislikes leading whitespace
            trackablesAsString = trackablesAsString.Substring(trackablesAsString.IndexOf('-'));

            return ParseStringToCustomList(trackablesAsString);
        }

        internal static List<Trackable> ParseStringToCustomList(string translationAsString)
        {
            var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

            List<Trackable> parsedList = deserializer.Deserialize<List<Trackable>>(translationAsString);

            if (parsedList == null || parsedList.Count == 0)
            {
                return null;
            }

            return parsedList;
        }

        internal static string ParseCustomDictToString(Dictionary<string, Trackable> data)
        {
            var serializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

            string parsedYaml = serializer.Serialize(data.Values.ToList());

            if (parsedYaml == null)
            {
                return null;
            }

            return parsedYaml;
        }

        public static string ReadEmbeddedTextFile(string path)
        {
            var assembly = Assembly.GetExecutingAssembly();
            Stream stream = assembly.GetManifestResourceStream(path);

            if (stream == null)
            {
                return null;
            }

            using (MemoryStream memStream = new MemoryStream())
            {
                stream.CopyTo(memStream);

                var bytes = memStream.Length > 0 ? memStream.ToArray() : null;

                return bytes != null ? Encoding.UTF8.GetString(bytes) : null;
            }
        }
    }
}