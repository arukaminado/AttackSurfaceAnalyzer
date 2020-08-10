﻿// Copyright (c) Microsoft Corporation. All rights reserved. Licensed under the MIT License.
using Microsoft.CST.AttackSurfaceAnalyzer.Collectors;
using Microsoft.CST.AttackSurfaceAnalyzer.Objects;
using Microsoft.CST.AttackSurfaceAnalyzer.Types;
using Microsoft.CST.AttackSurfaceAnalyzer.Utils;
using CommandLine;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.CST.OAT;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CST.AttackSurfaceAnalyzer.Cli
{
    public static class AttackSurfaceAnalyzerClient
    {
        private static List<BaseCollector> collectors = new List<BaseCollector>();
        private static readonly List<BaseMonitor> monitors = new List<BaseMonitor>();
        private static List<BaseCompare> comparators = new List<BaseCompare>();

        public static DatabaseManager DatabaseManager { get; private set; }

        private static void SetupLogging(CommandOptions opts)
        {
#if DEBUG
            Logger.Setup(true, opts.Verbose, opts.Quiet);
#else
            Logger.Setup(opts.Debug, opts.Verbose, opts.Quiet);
#endif
        }

        private static void SetupDatabase(CommandOptions opts)
        {
            var dbSettings = new DBSettings()
            {
                ShardingFactor = opts.Shards,
                LowMemoryUsage = opts.LowMemoryUsage
            };
            SetupOrDie(opts.DatabaseFilename, dbSettings);
        }

        private static void Main(string[] args)
        {
#if DEBUG
            Logger.Setup(true, false);
#else
            Logger.Setup(false, false);
#endif
            var version = (Assembly
                        .GetEntryAssembly()?
                        .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false) as AssemblyInformationalVersionAttribute[])?
                        [0].InformationalVersion ?? "Unknown";

            Log.Information("AttackSurfaceAnalyzer v.{0}", version);

            Strings.Setup();

            var argsResult = Parser.Default.ParseArguments<CollectCommandOptions, MonitorCommandOptions, ExportMonitorCommandOptions, ExportCollectCommandOptions, ConfigCommandOptions, GuiCommandOptions, VerifyOptions>(args)
                .MapResult(
                    (CollectCommandOptions opts) =>
                    {
                        SetupLogging(opts);
                        SetupDatabase(opts);
                        CheckFirstRun();
                        AsaTelemetry.SetEnabled(DatabaseManager.GetTelemetryEnabled());
                        return RunCollectCommand(opts);
                    },
                    (MonitorCommandOptions opts) =>
                    {
                        SetupLogging(opts);
                        SetupDatabase(opts);
                        CheckFirstRun();
                        AsaTelemetry.SetEnabled(DatabaseManager.GetTelemetryEnabled());
                        return RunMonitorCommand(opts);
                    },
                    (ExportCollectCommandOptions opts) =>
                    {
                        SetupLogging(opts);
                        SetupDatabase(opts);
                        CheckFirstRun();
                        AsaTelemetry.SetEnabled(DatabaseManager.GetTelemetryEnabled());
                        return RunExportCollectCommand(opts);
                    },
                    (ExportMonitorCommandOptions opts) =>
                    {
                        SetupLogging(opts);
                        SetupDatabase(opts);
                        CheckFirstRun();
                        AsaTelemetry.SetEnabled(DatabaseManager.GetTelemetryEnabled());
                        return RunExportMonitorCommand(opts);
                    },
                    (ConfigCommandOptions opts) =>
                    {
                        SetupLogging(opts);
                        return RunConfigCommand(opts);
                    },
                    (GuiCommandOptions opts) =>
                    {
                        SetupLogging(opts);
                        SetupDatabase(opts);
                        CheckFirstRun();
                        AsaTelemetry.SetEnabled(DatabaseManager.GetTelemetryEnabled());
                        return RunGuiCommand(opts);
                    },
                    (VerifyOptions opts) =>
                    {
                        SetupLogging(opts);
                        SetupDatabase(opts);
                        CheckFirstRun();
                        AsaTelemetry.SetEnabled(DatabaseManager.GetTelemetryEnabled());
                        return RunVerifyRulesCommand(opts);
                    },
                    (GuidedModeCommandOptions opts) =>
                    {
                        SetupLogging(opts);
                        SetupDatabase(opts);
                        CheckFirstRun();
                        AsaTelemetry.SetEnabled(DatabaseManager.GetTelemetryEnabled());
                        return RunGuidedModeCommand(opts);
                    },
                    errs => ASA_ERROR.UNKNOWN
                );
            DatabaseManager?.CloseDatabase();
            Log.CloseAndFlush();
            Environment.Exit((int)argsResult);
        }

        private static ASA_ERROR RunGuidedModeCommand(GuidedModeCommandOptions opts)
        {
            opts.RunId = opts.RunId?.Trim() ?? DateTime.Now.ToString("o", CultureInfo.InvariantCulture);

            var firstCollectRunId = $"{opts.RunId}-baseline";
            var secondCollectRunId = $"{opts.RunId}-after";
            var monitorRunId = $"{opts.RunId}-monitoring";

            var collectorOpts = CollectCommandOptions.FromCollectorOptions(opts);

            collectorOpts.RunId = firstCollectRunId;

            RunCollectCommand(collectorOpts);

            var monitorOpts = new MonitorCommandOptions()
            {
                Duration = opts.Duration,
                MonitoredDirectories = opts.MonitoredDirectories,
                EnableFileSystemMonitor = opts.EnableFileSystemMonitor,
                GatherHashes = opts.GatherHashes,
                FileNamesOnly = opts.FileNamesOnly,
                RunId = monitorRunId,
            };

            RunMonitorCommand(monitorOpts);

            collectorOpts.RunId = secondCollectRunId;

            RunCollectCommand(collectorOpts);

            var compareOpts = new CompareCommandOptions(firstCollectRunId, secondCollectRunId)
            {
                DisableAnalysis = opts.DisableAnalysis,
                AnalysesFile = opts.AnalysesFile,
                RunScripts = opts.RunScripts
            };

            var results = CompareRuns(compareOpts);

            if (opts.SaveToDatabase)
            {
                InsertCompareResults(results, firstCollectRunId, secondCollectRunId);
            }

            var monitorCompareOpts = new CompareCommandOptions(null, monitorRunId)
            {
                DisableAnalysis = opts.DisableAnalysis,
                AnalysesFile = opts.AnalysesFile,
                ApplySubObjectRulesToMonitor = opts.ApplySubObjectRulesToMonitor,
                RunScripts = opts.RunScripts
            };

            var monitorResult = AnalyzeMonitored(monitorCompareOpts);

            if (opts.SaveToDatabase)
            {
                InsertCompareResults(monitorResult, null, monitorRunId);
            }

            Parallel.ForEach(monitorResult.Keys, key =>
            {
                results.TryAdd(key, monitorResult[key]);
            });

            return ExportGuidedModeResults(results, opts);
        }

        public static ConcurrentDictionary<(RESULT_TYPE, CHANGE_TYPE), List<CompareResult>> AnalyzeMonitored(CompareCommandOptions opts)
        {
            if (opts is null) { return new ConcurrentDictionary<(RESULT_TYPE, CHANGE_TYPE), List<CompareResult>>(); }
            var analyzer = new AsaAnalyzer(new AnalyzerOptions(opts.RunScripts));
            var ruleFile = string.IsNullOrEmpty(opts.AnalysesFile) ? RuleFile.LoadEmbeddedFilters() : RuleFile.FromFile(opts.AnalysesFile);
            return AnalyzeMonitored(opts, analyzer, DatabaseManager.GetMonitorResults(opts.SecondRunId), ruleFile);
        }

        public static ConcurrentDictionary<(RESULT_TYPE, CHANGE_TYPE), List<CompareResult>> AnalyzeMonitored(CompareCommandOptions opts, AsaAnalyzer analyzer, IEnumerable<MonitorObject> collectObjects, RuleFile ruleFile)
        {
            if (opts is null) { return new ConcurrentDictionary<(RESULT_TYPE, CHANGE_TYPE), List<CompareResult>>(); }
            var results = new ConcurrentDictionary<(RESULT_TYPE, CHANGE_TYPE), List<CompareResult>>();
            Parallel.ForEach(collectObjects, monitorResult =>
            {
                var shellResult = new CompareResult()
                {
                    Compare = monitorResult,
                    CompareRunId = opts.SecondRunId
                };

                shellResult.Rules = analyzer.Analyze(ruleFile.AsaRules, shellResult).ToList();

                if (opts.ApplySubObjectRulesToMonitor)
                {
                    switch (monitorResult)
                    {
                        case FileMonitorObject fmo:
                            var innerShell = new CompareResult()
                            {
                                Compare = fmo.FileSystemObject,
                                CompareRunId = opts.SecondRunId
                            };
                            shellResult.Rules.AddRange(analyzer.Analyze(ruleFile.AsaRules, innerShell));
                            break;
                    }
                }

                shellResult.Analysis = shellResult.Rules.Count > 0 ? shellResult.Rules.Max(x => ((AsaRule)x).Flag) : ruleFile.DefaultLevels[shellResult.ResultType];

                results.TryAdd((monitorResult.ResultType, monitorResult.ChangeType), new List<CompareResult>());
                results[(monitorResult.ResultType, monitorResult.ChangeType)].Add(shellResult);
            });
            return results;
        }

        private static ASA_ERROR RunVerifyRulesCommand(VerifyOptions opts)
        {
            var analyzer = new AsaAnalyzer(new AnalyzerOptions(opts.RunScripts));
            var ruleFile = string.IsNullOrEmpty(opts.AnalysisFile) ? RuleFile.LoadEmbeddedFilters() : RuleFile.FromFile(opts.AnalysisFile);
            var violations = analyzer.EnumerateRuleIssues(ruleFile.GetRules());
            Analyzer.PrintViolations(violations);
            if (violations.Any())
            {
                Log.Error("Encountered {0} issues with rules at {1}", violations.Count(), opts.AnalysisFile ?? "Embedded");
                return ASA_ERROR.INVALID_RULES;
            }
            Log.Information("{0} Rules successfully verified. ✅", ruleFile.AsaRules.Count());
            return ASA_ERROR.NONE;
        }

        internal static void InsertCompareResults(ConcurrentDictionary<(RESULT_TYPE, CHANGE_TYPE), List<CompareResult>> results, string? FirstRunId, string SecondRunId)
        {
            DatabaseManager.InsertCompareRun(FirstRunId, SecondRunId, RUN_STATUS.RUNNING);
            foreach (var key in results.Keys)
            {
                if (results.TryGetValue(key, out List<CompareResult>? obj))
                {
                    if (obj is List<CompareResult> Queue)
                    {
                        foreach (var result in Queue)
                        {
                            DatabaseManager.InsertAnalyzed(result);
                        }
                    }
                }
            }
            DatabaseManager.UpdateCompareRun(FirstRunId, SecondRunId, RUN_STATUS.COMPLETED);

            DatabaseManager.Commit();
        }

        private static void SetupOrDie(string path, DBSettings? dbSettingsIn = null)
        {
            DatabaseManager = new SqliteDatabaseManager(path, dbSettingsIn);
            var errorCode = DatabaseManager.Setup();

            if (errorCode != ASA_ERROR.NONE)
            {
                Log.Fatal(Strings.Get("CouldNotSetupDatabase"));
                Environment.Exit((int)errorCode);
            }
        }

        private static ASA_ERROR RunGuiCommand(GuiCommandOptions opts)
        {
            var server = Host.CreateDefaultBuilder(Array.Empty<string>())
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                })
                .Build();

            ((Action)(async () =>
            {
                await Task.Run(() => SleepAndOpenBrowser(1500)).ConfigureAwait(false);
            }))();

            server.Run();
            return 0;
        }

        private static void SleepAndOpenBrowser(int sleep)
        {
            Thread.Sleep(sleep);
            AsaHelpers.OpenBrowser(new System.Uri("http://localhost:5000")); /*DevSkim: ignore DS137138*/
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2241:Provide correct arguments to formatting methods", Justification = "<Pending>")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1305:Specify IFormatProvider", Justification = "<Pending>")]
        private static ASA_ERROR RunConfigCommand(ConfigCommandOptions opts)
        {
            if (opts.ResetDatabase)
            {
                var filename = opts.DatabaseFilename;
                DatabaseManager.Destroy(opts.DatabaseFilename);
                Log.Information(Strings.Get("DeletedDatabaseAt"), filename);
            }
            else
            {
                SetupDatabase(opts);
                CheckFirstRun();

                if (opts.ListRuns)
                {
                    if (DatabaseManager.FirstRun)
                    {
                        Log.Warning(Strings.Get("FirstRunListRunsError"), opts.DatabaseFilename);
                    }
                    else
                    {
                        Log.Information(Strings.Get("DumpingDataFromDatabase"), opts.DatabaseFilename);
                        List<string> CollectRuns = DatabaseManager.GetRuns(RUN_TYPE.COLLECT);
                        if (CollectRuns.Count > 0)
                        {
                            Log.Information(Strings.Get("Begin"), Strings.Get("EnumeratingCollectRunIds"));
                            foreach (string runId in CollectRuns)
                            {
                                var run = DatabaseManager.GetRun(runId);

                                if (run is AsaRun)
                                {
                                    Log.Information("RunId:{2} Timestamp:{0} AsaVersion:{1} ",
                                    run.Timestamp,
                                    run.Version,
                                    run.RunId);

                                    var resultTypesAndCounts = DatabaseManager.GetResultTypesAndCounts(run.RunId);

                                    foreach (var kvPair in resultTypesAndCounts)
                                    {
                                        Log.Information("{0} : {1}", kvPair.Key, kvPair.Value);
                                    }
                                }
                            }
                        }
                        else
                        {
                            Log.Information(Strings.Get("NoCollectRuns"));
                        }

                        List<string> MonitorRuns = DatabaseManager.GetRuns(RUN_TYPE.MONITOR);
                        if (MonitorRuns.Count > 0)
                        {
                            Log.Information(Strings.Get("Begin"), Strings.Get("EnumeratingMonitorRunIds"));

                            foreach (string monitorRun in MonitorRuns)
                            {
                                var run = DatabaseManager.GetRun(monitorRun);

                                if (run != null)
                                {
                                    string output = $"{run.RunId} {run.Timestamp} {run.Version} {run.Type}";
                                    Log.Information(output);
                                    Log.Information(string.Join(',', run.ResultTypes.Where(x => run.ResultTypes.Contains(x))));
                                }
                            }
                        }
                        else
                        {
                            Log.Information(Strings.Get("NoMonitorRuns"));
                        }
                    }
                }

                AsaTelemetry.SetEnabled(opts.TelemetryOptOut);
                Log.Information(Strings.Get("TelemetryOptOut"), opts.TelemetryOptOut ? "Opted out" : "Opted in");

                if (opts.DeleteRunId != null)
                {
                    DatabaseManager.DeleteRun(opts.DeleteRunId);
                }
                if (opts.TrimToLatest)
                {
                    DatabaseManager.TrimToLatest();
                }
            }
            return ASA_ERROR.NONE;
        }

        private static ASA_ERROR RunExportCollectCommand(ExportCollectCommandOptions opts)
        {
            if (opts.OutputPath != null && !Directory.Exists(opts.OutputPath))
            {
                Log.Fatal(Strings.Get("Err_OutputPathNotExist"), opts.OutputPath);
                return 0;
            }

            if (opts.ExportSingleRun)
            {
                if (opts.SecondRunId is null)
                {
                    Log.Information("Provided null second run id using latest run.");
                    List<string> runIds = DatabaseManager.GetLatestRunIds(1, RUN_TYPE.COLLECT);
                    if (runIds.Count < 1)
                    {
                        Log.Fatal(Strings.Get("Err_CouldntDetermineOneRun"));
                        return ASA_ERROR.INVALID_ID;
                    }
                    else
                    {
                        // If you ask for single run everything is "Created"
                        opts.SecondRunId = runIds.First();
                        opts.FirstRunId = null;
                    }
                }
            }
            else if (opts.FirstRunId is null || opts.SecondRunId is null)
            {
                Log.Information("Provided null run Ids using latest two runs.");
                List<string> runIds = DatabaseManager.GetLatestRunIds(2, RUN_TYPE.COLLECT);

                if (runIds.Count < 2)
                {
                    Log.Fatal(Strings.Get("Err_CouldntDetermineTwoRun"));
                    System.Environment.Exit(-1);
                }
                else
                {
                    opts.SecondRunId = runIds.First();
                    opts.FirstRunId = runIds.ElementAt(1);
                }
            }

            Log.Information(Strings.Get("Comparing"), opts.FirstRunId, opts.SecondRunId);

            Dictionary<string, string> StartEvent = new Dictionary<string, string>();
            StartEvent.Add("OutputPathSet", (opts.OutputPath != null).ToString(CultureInfo.InvariantCulture));

            AsaTelemetry.TrackEvent("{0} Export Compare", StartEvent);

            CompareCommandOptions options = new CompareCommandOptions(opts.FirstRunId, opts.SecondRunId)
            {
                DatabaseFilename = opts.DatabaseFilename,
                AnalysesFile = opts.AnalysesFile,
                DisableAnalysis = opts.DisableAnalysis,
                SaveToDatabase = opts.SaveToDatabase,
                RunScripts = opts.RunScripts
            };

            var results = CompareRuns(options);

            if (opts.SaveToDatabase)
            {
                InsertCompareResults(results, opts.FirstRunId, opts.SecondRunId);
            }

            return ExportCompareResults(results, opts, AsaHelpers.MakeValidFileName(opts.FirstRunId + "_vs_" + opts.SecondRunId));
        }

        private static ASA_ERROR ExportGuidedModeResults(ConcurrentDictionary<(RESULT_TYPE, CHANGE_TYPE), List<CompareResult>> resultsIn, GuidedModeCommandOptions opts)
        {
            if (opts.RunId == null)
            {
                return ASA_ERROR.INVALID_ID;
            }
            var results = resultsIn.Select(x => new KeyValuePair<string, object>($"{x.Key.Item1}_{x.Key.Item2}", x.Value)).ToDictionary(x => x.Key, x => x.Value);
            JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings()
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                Converters = new List<JsonConverter>() { new StringEnumConverter() },
                ContractResolver = new AsaExportContractResolver()
            });
            var outputPath = opts.OutputPath;
            if (outputPath is null)
            {
                outputPath = Directory.GetCurrentDirectory();
            }
            if (opts.ExplodedOutput)
            {
                results.Add("metadata", AsaHelpers.GenerateMetadata());

                string path = Path.Combine(outputPath, AsaHelpers.MakeValidFileName(opts.RunId));
                Directory.CreateDirectory(path);
                foreach (var key in results.Keys)
                {
                    string filePath = Path.Combine(path, AsaHelpers.MakeValidFileName(key));
                    using (StreamWriter sw = new StreamWriter(filePath)) //lgtm[cs/path-injection]
                    {
                        using (JsonWriter writer = new JsonTextWriter(sw))
                        {
                            serializer.Serialize(writer, results[key]);
                        }
                    }
                }
                Log.Information(Strings.Get("OutputWrittenTo"), (new DirectoryInfo(path)).FullName);
            }
            else
            {
                string path = Path.Combine(outputPath, AsaHelpers.MakeValidFileName(opts.RunId + "_summary.json.txt"));
                var output = new Dictionary<string, object>();
                output["results"] = results;
                output["metadata"] = AsaHelpers.GenerateMetadata();
                using (StreamWriter sw = new StreamWriter(path)) //lgtm[cs/path-injection]
                {
                    using (JsonWriter writer = new JsonTextWriter(sw))
                    {
                        serializer.Serialize(writer, output);
                    }
                }
                Log.Information(Strings.Get("OutputWrittenTo"), (new FileInfo(path)).FullName);
            }
            return ASA_ERROR.NONE;
        }

        private static ASA_ERROR ExportCompareResults(ConcurrentDictionary<(RESULT_TYPE, CHANGE_TYPE), List<CompareResult>> resultsIn, ExportOptions opts, string baseFileName)
        {
            var results = resultsIn.Select(x => new KeyValuePair<string, object>($"{x.Key.Item1}_{x.Key.Item2}", x.Value)).ToDictionary(x => x.Key, x => x.Value);
            JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings()
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                Converters = new List<JsonConverter>() { new StringEnumConverter() },
                ContractResolver = new AsaExportContractResolver()
            });
            var outputPath = opts.OutputPath;
            if (outputPath is null)
            {
                outputPath = Directory.GetCurrentDirectory();
            }
            if (opts.ExplodedOutput)
            {
                results.Add("metadata", AsaHelpers.GenerateMetadata());

                string path = Path.Combine(outputPath, AsaHelpers.MakeValidFileName(baseFileName));
                Directory.CreateDirectory(path);
                foreach (var key in results.Keys)
                {
                    string filePath = Path.Combine(path, AsaHelpers.MakeValidFileName(key));
                    using (StreamWriter sw = new StreamWriter(filePath)) //lgtm[cs/path-injection]
                    {
                        using (JsonWriter writer = new JsonTextWriter(sw))
                        {
                            serializer.Serialize(writer, results[key]);
                        }
                    }
                }
                Log.Information(Strings.Get("OutputWrittenTo"), (new DirectoryInfo(path)).FullName);
            }
            else
            {
                string path = Path.Combine(outputPath, AsaHelpers.MakeValidFileName(baseFileName + "_summary.json.txt"));
                var output = new Dictionary<string, object>();
                output["results"] = results;
                output["metadata"] = AsaHelpers.GenerateMetadata();
                using (StreamWriter sw = new StreamWriter(path)) //lgtm[cs/path-injection]
                {
                    using (JsonWriter writer = new JsonTextWriter(sw))
                    {
                        serializer.Serialize(writer, output);
                    }
                }
                Log.Information(Strings.Get("OutputWrittenTo"), (new FileInfo(path)).FullName);
            }
            return ASA_ERROR.NONE;
        }

        private class AsaExportContractResolver : DefaultContractResolver
        {
            public static readonly AsaExportContractResolver Instance = new AsaExportContractResolver();

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                JsonProperty property = base.CreateProperty(member, memberSerialization);

                if (property.DeclaringType == typeof(RegistryObject))
                {
                    if (property.PropertyName == "Subkeys" || property.PropertyName == "Values")
                    {
                        property.ShouldSerialize = _ => { return false; };
                    }
                }

                if (property.DeclaringType == typeof(Rule))
                {
                    if (property.PropertyName != "Name" && property.PropertyName != "Description" && property.PropertyName != "Flag")
                    {
                        property.ShouldSerialize = _ => { return false; };
                    }
                }

                return property;
            }
        }

        public static void WriteScanJson(int ResultType, string BaseId, string CompareId, bool ExportAll, string OutputPath)
        {
            var invalidFileNameChars = Path.GetInvalidPathChars().ToList();
            OutputPath = new string(OutputPath.Select(ch => invalidFileNameChars.Contains(ch) ? Convert.ToChar(invalidFileNameChars.IndexOf(ch) + 65) : ch).ToArray());
            List<RESULT_TYPE> ToExport = new List<RESULT_TYPE> { (RESULT_TYPE)ResultType };
            Dictionary<RESULT_TYPE, int> actualExported = new Dictionary<RESULT_TYPE, int>();
            JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings()
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                Converters = new List<JsonConverter>() { new StringEnumConverter() }
            });
            if (ExportAll)
            {
                ToExport = new List<RESULT_TYPE> { RESULT_TYPE.FILE, RESULT_TYPE.CERTIFICATE, RESULT_TYPE.PORT, RESULT_TYPE.REGISTRY, RESULT_TYPE.SERVICE, RESULT_TYPE.USER };
            }

            foreach (RESULT_TYPE ExportType in ToExport)
            {
                Log.Information("Exporting {0}", ExportType);
                List<CompareResult> records = DatabaseManager.GetComparisonResults(BaseId, CompareId, ExportType);

                actualExported.Add(ExportType, records.Count);

                if (records.Count > 0)
                {
                    serializer.Converters.Add(new StringEnumConverter());
                    var o = new Dictionary<string, Object>();
                    o["results"] = records;
                    o["metadata"] = AsaHelpers.GenerateMetadata();
                    using (StreamWriter sw = new StreamWriter(Path.Combine(OutputPath, AsaHelpers.MakeValidFileName(BaseId + "_vs_" + CompareId + "_" + ExportType.ToString() + ".json.txt")))) //lgtm [cs/path-injection]
                    {
                        using (JsonWriter writer = new JsonTextWriter(sw))
                        {
                            serializer.Serialize(writer, o);
                        }
                    }
                }
            }

            serializer.Converters.Add(new StringEnumConverter());
            var output = new Dictionary<string, Object>();
            output["results"] = actualExported;
            output["metadata"] = AsaHelpers.GenerateMetadata();
            using (StreamWriter sw = new StreamWriter(Path.Combine(OutputPath, AsaHelpers.MakeValidFileName(BaseId + "_vs_" + CompareId + "_summary.json.txt")))) //lgtm [cs/path-injection]
            {
                using (JsonWriter writer = new JsonTextWriter(sw))
                {
                    serializer.Serialize(writer, output);
                }
            }
        }

        private static void CheckFirstRun()
        {
            if (DatabaseManager == null || DatabaseManager.FirstRun)
            {
                string exeStr = $"config --telemetry-opt-out";
                Log.Information(Strings.Get("ApplicationHasTelemetry"));
                Log.Information(Strings.Get("ApplicationHasTelemetry2"), "https://github.com/Microsoft/AttackSurfaceAnalyzer/blob/master/PRIVACY.md");
                Log.Information(Strings.Get("ApplicationHasTelemetry3"), exeStr);
            }
        }

        private static ASA_ERROR RunExportMonitorCommand(ExportMonitorCommandOptions opts)
        {
            if (opts.RunId is null)
            {
                var runIds = DatabaseManager.GetLatestRunIds(1, RUN_TYPE.MONITOR);
                if (runIds.Any())
                {
                    opts.RunId = runIds.First();
                }
                else
                {
                    Log.Fatal(Strings.Get("Err_CouldntDetermineOneRun"));
                    return ASA_ERROR.INVALID_ID;
                }
            }
            var monitorCompareOpts = new CompareCommandOptions(null, opts.RunId)
            {
                DisableAnalysis = opts.DisableAnalysis,
                AnalysesFile = opts.AnalysesFile,
                ApplySubObjectRulesToMonitor = opts.ApplySubObjectRulesToMonitor,
                RunScripts = opts.RunScripts
            };

            var monitorResult = AnalyzeMonitored(monitorCompareOpts);

            if (opts.SaveToDatabase)
            {
                InsertCompareResults(monitorResult, null, opts.RunId);
            }

            return ExportCompareResults(monitorResult, opts, AsaHelpers.MakeValidFileName(opts.RunId));
        }

        public static void WriteMonitorJson(string RunId, int ResultType, string OutputPath)
        {
            var invalidFileNameChars = Path.GetInvalidPathChars().ToList();
            OutputPath = new string(OutputPath.Select(ch => invalidFileNameChars.Contains(ch) ? Convert.ToChar(invalidFileNameChars.IndexOf(ch) + 65) : ch).ToArray());

            List<FileMonitorEvent> records = DatabaseManager.GetSerializedMonitorResults(RunId);

            JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings()
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                Converters = new List<JsonConverter>() { new StringEnumConverter() }
            });
            var output = new Dictionary<string, Object>();
            output["results"] = records;
            output["metadata"] = AsaHelpers.GenerateMetadata();
            string path = Path.Combine(OutputPath, AsaHelpers.MakeValidFileName(RunId + "_Monitoring_" + ((RESULT_TYPE)ResultType).ToString() + ".json.txt"));

            using (StreamWriter sw = new StreamWriter(path)) //lgtm [cs/path-injection]
            using (JsonWriter writer = new JsonTextWriter(sw))
            {
                serializer.Serialize(writer, output);
            }

            Log.Information(Strings.Get("OutputWrittenTo"), (new FileInfo(path)).FullName);
        }

        private static ASA_ERROR RunMonitorCommand(MonitorCommandOptions opts)
        {
            Dictionary<string, string> StartEvent = new Dictionary<string, string>();
            StartEvent.Add("Files", opts.EnableFileSystemMonitor.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Admin", AsaHelpers.IsAdmin().ToString(CultureInfo.InvariantCulture));
            AsaTelemetry.TrackEvent("Begin monitoring", StartEvent);

            if (opts.RunId is string)
            {
                opts.RunId = opts.RunId.Trim();
            }
            else
            {
                opts.RunId = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
            }

            if (opts.Overwrite)
            {
                DatabaseManager.DeleteRun(opts.RunId);
            }
            else
            {
                if (DatabaseManager.GetRun(opts.RunId) != null)
                {
                    Log.Error(Strings.Get("Err_RunIdAlreadyUsed"));
                    return ASA_ERROR.UNIQUE_ID;
                }
            }
            var run = new AsaRun(RunId: opts.RunId, Timestamp: DateTime.Now, Version: AsaHelpers.GetVersionString(), Platform: AsaHelpers.GetPlatform(), new List<RESULT_TYPE>() { RESULT_TYPE.FILEMONITOR }, RUN_TYPE.MONITOR);

            DatabaseManager.InsertRun(run);

            var returnValue = ASA_ERROR.NONE;

            if (opts.EnableFileSystemMonitor)
            {
                monitors.Add(new FileSystemMonitor(opts, x => DatabaseManager.Write(x, opts.RunId)));
            }

            if (monitors.Count == 0)
            {
                Log.Warning(Strings.Get("Err_NoMonitors"));
                returnValue = ASA_ERROR.NO_COLLECTORS;
            }

            using var exitEvent = new ManualResetEvent(false);

            // If duration is set, we use the secondary timer.
            if (opts.Duration > 0)
            {
                Log.Information("{0} {1} {2}.", Strings.Get("MonitorStartedFor"), opts.Duration, Strings.Get("Minutes"));
                using var aTimer = new System.Timers.Timer
                {
                    Interval = opts.Duration * 60 * 1000, //lgtm [cs/loss-of-precision]
                    AutoReset = false,
                };
                aTimer.Elapsed += (source, e) => { exitEvent.Set(); };

                // Start the timer
                aTimer.Enabled = true;
            }

            foreach (FileSystemMonitor c in monitors)
            {
                Log.Information(Strings.Get("Begin"), c.GetType().Name);

                try
                {
                    c.StartRun();
                }
                catch (Exception ex)
                {
                    Log.Error(Strings.Get("Err_CollectingFrom"), c.GetType().Name, ex.Message, ex.StackTrace);
                    returnValue = ASA_ERROR.UNKNOWN;
                }
            }

            void consoleCancelDelegate(object sender, ConsoleCancelEventArgs args)
            {
                args.Cancel = true;
                exitEvent.Set();
            };
            // Set up the event to capture CTRL+C
            Console.CancelKeyPress += consoleCancelDelegate;

            Console.Write(Strings.Get("MonitoringPressC"));

            // Write a spinner and wait until CTRL+C
            WriteSpinner(exitEvent);
            Log.Information("");

            foreach (var c in monitors)
            {
                Log.Information(Strings.Get("End"), c.GetType().Name);

                try
                {
                    c.StopRun();
                    if (c is FileSystemMonitor)
                    {
                        ((FileSystemMonitor)c).Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, " {0}: {1}", c.GetType().Name, ex.Message, Strings.Get("Err_Stopping"));
                }
            }

            FlushResults();

            DatabaseManager.Commit();

            Console.CancelKeyPress -= consoleCancelDelegate;

            return returnValue;
        }

        public static List<BaseCollector> GetCollectors()
        {
            return collectors;
        }

        public static List<BaseMonitor> GetMonitors()
        {
            return monitors;
        }

        public static List<BaseCompare> GetComparators()
        {
            return comparators;
        }

        public static ConcurrentDictionary<(RESULT_TYPE, CHANGE_TYPE), List<CompareResult>> CompareRuns(CompareCommandOptions opts)
        {
            if (opts is null)
            {
                throw new ArgumentNullException(nameof(opts));
            }

            comparators = new List<BaseCompare>();

            Dictionary<string, string> EndEvent = new Dictionary<string, string>();
            BaseCompare c = new BaseCompare();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (!c.TryCompare(opts.FirstRunId, opts.SecondRunId, DatabaseManager))
            {
                Log.Warning(Strings.Get("Err_Comparing") + " : {0}", c.GetType().Name);
            }

            watch.Stop();
            TimeSpan t = TimeSpan.FromMilliseconds(watch.ElapsedMilliseconds);
            string answer = string.Format(CultureInfo.InvariantCulture, "{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                                    t.Hours,
                                    t.Minutes,
                                    t.Seconds,
                                    t.Milliseconds);

            Log.Information(Strings.Get("Completed"), "Comparing", answer);

            if (!opts.DisableAnalysis)
            {
                watch = Stopwatch.StartNew();
                var ruleFile = string.IsNullOrEmpty(opts.AnalysesFile) ? RuleFile.LoadEmbeddedFilters() : RuleFile.FromFile(opts.AnalysesFile);
                var analyzer = new AsaAnalyzer(new AnalyzerOptions(opts.RunScripts));
                var platform = DatabaseManager.RunIdToPlatform(opts.SecondRunId);
                var violations = analyzer.EnumerateRuleIssues(ruleFile.GetRules());
                Analyzer.PrintViolations(violations);
                if (violations.Any())
                {
                    Log.Error("Encountered {0} issues with rules in {1}. Skipping analysis.", violations.Count(), opts.AnalysesFile ?? "Embedded");
                }
                else
                {
                    if (c.Results.Count > 0)
                    {
                        foreach (var key in c.Results.Keys)
                        {
                            if (c.Results[key] is List<CompareResult> queue)
                            {
                                queue.AsParallel().ForAll(res =>
                                {
                                    // Select rules with the appropriate change type, platform and target
                                    // - Target is also checked inside Analyze, but this shortcuts repeatedly
                                    // checking rules which don't apply
                                    var selectedRules = ruleFile.AsaRules.Where((rule) =>
                                        (rule.ChangeTypes == null || rule.ChangeTypes.Contains(res.ChangeType))
                                            && (rule.Platforms == null || rule.Platforms.Contains(platform))
                                            && (rule.ResultType == res.ResultType));
                                    res.Rules = analyzer.Analyze(selectedRules, res.Base, res.Compare).ToList();
                                    res.Analysis = res.Rules.Count
                                                   > 0 ? res.Rules.Max(x => ((AsaRule)x).Flag) : ruleFile.DefaultLevels[res.ResultType];
                                });
                            }
                        }
                    }
                }

                watch.Stop();
                t = TimeSpan.FromMilliseconds(watch.ElapsedMilliseconds);
                answer = string.Format(CultureInfo.InvariantCulture, "{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                                        t.Hours,
                                        t.Minutes,
                                        t.Seconds,
                                        t.Milliseconds);
                Log.Information(Strings.Get("Completed"), "Analysis", answer);
            }

            AsaTelemetry.TrackEvent("End Command", EndEvent);
            return c.Results;
        }

        public static ASA_ERROR RunGuiMonitorCommand(MonitorCommandOptions opts)
        {
            if (opts is null)
            {
                return ASA_ERROR.NO_COLLECTORS;
            }
            if (opts.EnableFileSystemMonitor)
            {
                monitors.Add(new FileSystemMonitor(opts, x => DatabaseManager.Write(x, opts.RunId)));
            }

            if (monitors.Count == 0)
            {
                Log.Warning(Strings.Get("Err_NoMonitors"));
            }

            foreach (var c in monitors)
            {
                c.StartRun();
            }

            return ASA_ERROR.NONE;
        }

        public static int StopMonitors()
        {
            foreach (var c in monitors)
            {
                Log.Information(Strings.Get("End"), c.GetType().Name);

                c.StopRun();
            }

            FlushResults();

            DatabaseManager.Commit();

            return 0;
        }

        public static void AdminOrWarn()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!Elevation.IsAdministrator())
                {
                    Log.Warning(Strings.Get("Err_RunAsAdmin"));
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (!Elevation.IsRunningAsRoot())
                {
                    Log.Warning(Strings.Get("Err_RunAsRoot"));
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (!Elevation.IsRunningAsRoot())
                {
                    Log.Warning(Strings.Get("Err_RunAsRoot"));
                }
            }
        }

        public static ASA_ERROR RunCollectCommand(CollectCommandOptions opts)
        {
            if (opts == null) { return ASA_ERROR.NO_COLLECTORS; }
            collectors.Clear();

            Dictionary<string, string> StartEvent = new Dictionary<string, string>();
            StartEvent.Add("Files", opts.EnableAllCollectors ? "True" : opts.EnableFileSystemCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Ports", opts.EnableNetworkPortCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Users", opts.EnableUserCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Certificates", opts.EnableCertificateCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Registry", opts.EnableRegistryCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Service", opts.EnableServiceCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Firewall", opts.EnableFirewallCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("ComObject", opts.EnableComObjectCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("EventLog", opts.EnableEventLogCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Tpm", opts.EnableEventLogCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Keys", opts.EnableKeyCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Drivers", opts.EnableDriverCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Process", opts.EnableProcessCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Wifi", opts.EnableWifiCollector.ToString(CultureInfo.InvariantCulture));
            StartEvent.Add("Admin", AsaHelpers.IsAdmin().ToString(CultureInfo.InvariantCulture));
            AsaTelemetry.TrackEvent("Run Command", StartEvent);

            AdminOrWarn();

            opts.RunId = opts.RunId?.Trim() ?? DateTime.Now.ToString("o", CultureInfo.InvariantCulture);

            if (opts.MatchedCollectorId != null)
            {
                var matchedRun = DatabaseManager.GetRun(opts.MatchedCollectorId);
                if (matchedRun is AsaRun)
                {
                    foreach (var resultType in matchedRun.ResultTypes)
                    {
                        switch (resultType)
                        {
                            case RESULT_TYPE.FILE:
                                opts.EnableFileSystemCollector = true;
                                break;

                            case RESULT_TYPE.PORT:
                                opts.EnableNetworkPortCollector = true;
                                break;

                            case RESULT_TYPE.CERTIFICATE:
                                opts.EnableCertificateCollector = true;
                                break;

                            case RESULT_TYPE.COM:
                                opts.EnableComObjectCollector = true;
                                break;

                            case RESULT_TYPE.FIREWALL:
                                opts.EnableFirewallCollector = true;
                                break;

                            case RESULT_TYPE.LOG:
                                opts.EnableEventLogCollector = true;
                                break;

                            case RESULT_TYPE.SERVICE:
                                opts.EnableServiceCollector = true;
                                break;

                            case RESULT_TYPE.USER:
                                opts.EnableUserCollector = true;
                                break;

                            case RESULT_TYPE.KEY:
                                opts.EnableKeyCollector = true;
                                break;

                            case RESULT_TYPE.TPM:
                                opts.EnableTpmCollector = true;
                                break;

                            case RESULT_TYPE.PROCESS:
                                opts.EnableProcessCollector = true;
                                break;

                            case RESULT_TYPE.DRIVER:
                                opts.EnableDriverCollector = true;
                                break;

                            case RESULT_TYPE.WIFI:
                                opts.EnableWifiCollector = true;
                                break;
                        }
                    }
                }
            }

            Action<CollectObject> defaultChangeHandler = x => DatabaseManager.Write(x, opts.RunId);

            var dict = new List<RESULT_TYPE>();

            if (opts.EnableFileSystemCollector || opts.EnableAllCollectors)
            {
                collectors.Add(new FileSystemCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.FILE);
            }
            if (opts.EnableNetworkPortCollector || opts.EnableAllCollectors)
            {
                collectors.Add(new OpenPortCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.PORT);
            }
            if (opts.EnableServiceCollector || opts.EnableAllCollectors)
            {
                collectors.Add(new ServiceCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.SERVICE);
            }
            if (opts.EnableUserCollector || opts.EnableAllCollectors)
            {
                collectors.Add(new UserAccountCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.USER);
            }
            if (opts.EnableRegistryCollector || (opts.EnableAllCollectors && RuntimeInformation.IsOSPlatform(OSPlatform.Windows)))
            {
                collectors.Add(new RegistryCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.REGISTRY);
            }
            if (opts.EnableCertificateCollector || opts.EnableAllCollectors)
            {
                collectors.Add(new CertificateCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.CERTIFICATE);
            }
            if (opts.EnableFirewallCollector || opts.EnableAllCollectors)
            {
                collectors.Add(new FirewallCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.FIREWALL);
            }
            if (opts.EnableComObjectCollector || (opts.EnableAllCollectors && RuntimeInformation.IsOSPlatform(OSPlatform.Windows)))
            {
                collectors.Add(new ComObjectCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.COM);
            }
            if (opts.EnableEventLogCollector || opts.EnableAllCollectors)
            {
                collectors.Add(new EventLogCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.LOG);
            }
            if (opts.EnableTpmCollector || (opts.EnableAllCollectors && (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))))
            {
                collectors.Add(new TpmCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.TPM);
            }
            if (opts.EnableKeyCollector || opts.EnableAllCollectors && (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)))
            {
                collectors.Add(new CryptographicKeyCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.KEY);
            }
            if (opts.EnableProcessCollector || opts.EnableAllCollectors)
            {
                collectors.Add(new ProcessCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.PROCESS);
            }
            if (opts.EnableDriverCollector || opts.EnableAllCollectors)
            {
                collectors.Add(new DriverCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.DRIVER);
            }
            if (opts.EnableWifiCollector || opts.EnableAllCollectors)
            {
                collectors.Add(new WifiCollector(opts, defaultChangeHandler));
                dict.Add(RESULT_TYPE.WIFI);
            }

            if (collectors.Count == 0)
            {
                Log.Warning(Strings.Get("Err_NoCollectors"));
                return ASA_ERROR.NO_COLLECTORS;
            }

            if (opts.Overwrite)
            {
                DatabaseManager.DeleteRun(opts.RunId);
            }
            else
            {
                if (DatabaseManager.GetRun(opts.RunId) != null)
                {
                    Log.Error(Strings.Get("Err_RunIdAlreadyUsed"));
                    return ASA_ERROR.UNIQUE_ID;
                }
            }
            Log.Information(Strings.Get("Begin"), opts.RunId);

            var run = new AsaRun(RunId: opts.RunId, Timestamp: DateTime.Now, Version: AsaHelpers.GetVersionString(), Platform: AsaHelpers.GetPlatform(), ResultTypes: dict, Type: RUN_TYPE.COLLECT);

            DatabaseManager.InsertRun(run);

            Log.Information(Strings.Get("StartingN"), collectors.Count.ToString(CultureInfo.InvariantCulture), Strings.Get("Collectors"));

            using CancellationTokenSource source = new CancellationTokenSource();
            CancellationToken token = source.Token;

            void cancelKeyDelegate(object sender, ConsoleCancelEventArgs args)
            {
                Log.Information("Cancelling collection. Rolling back transaction. Please wait to avoid corrupting database.");
                source.Cancel();
                DatabaseManager.CloseDatabase();
                Environment.Exit((int)ASA_ERROR.CANCELLED);
            }
            Console.CancelKeyPress += cancelKeyDelegate;

            Dictionary<string, string> EndEvent = new Dictionary<string, string>();
            foreach (BaseCollector c in collectors)
            {
                try
                {
                    DatabaseManager.BeginTransaction();

                    c.TryExecute(token);

                    FlushResults();

                    DatabaseManager.Commit();
                }
                catch (Exception e)
                {
                    Log.Error(Strings.Get("Err_CollectingFrom"), c.GetType().Name, e.Message, e.StackTrace);
                    Dictionary<string, string> ExceptionEvent = new Dictionary<string, string>();
                    ExceptionEvent.Add("Exception Type", e.GetType().ToString());
                    ExceptionEvent.Add("Stack Trace", e.StackTrace ?? string.Empty);
                    ExceptionEvent.Add("Message", e.Message);
                    AsaTelemetry.TrackEvent("CollectorCrashRogueException", ExceptionEvent);
                    Console.CancelKeyPress -= cancelKeyDelegate;

                    return ASA_ERROR.FAILED_TO_COMMIT;
                }
            }
            AsaTelemetry.TrackEvent("End Command", EndEvent);

            DatabaseManager.Commit();
            Console.CancelKeyPress -= cancelKeyDelegate;

            return ASA_ERROR.NONE;
        }

        private static void FlushResults()
        {
            var prevFlush = DatabaseManager.QueueSize;
            var totFlush = prevFlush;

            var printInterval = new TimeSpan(0, 0, 10);
            var then = DateTime.Now;

            var StopWatch = Stopwatch.StartNew();
            TimeSpan t = new TimeSpan();
            string answer = string.Empty;
            bool warnedToIncreaseShards = false;
            var settings = DatabaseManager.GetCurrentSettings();

            while (DatabaseManager.HasElements)
            {
                Thread.Sleep(100);
                if (!DatabaseManager.HasElements)
                {
                    break;
                }
                if (!warnedToIncreaseShards && StopWatch.ElapsedMilliseconds > 10000 && settings.ShardingFactor < 7)
                {
                    Log.Information("It is taking a while to flush results to the database.  Try increasing the sharding level to improve performance.");
                    warnedToIncreaseShards = true;
                }
                var now = DateTime.Now;
                if (now - then > printInterval)
                {
                    var actualDuration = now - then;
                    var sample = DatabaseManager.QueueSize;
                    var curRate = prevFlush - sample;
                    var totRate = (double)(totFlush - sample) / StopWatch.ElapsedMilliseconds;

                    try
                    {
                        t = (curRate > 0) ? TimeSpan.FromMilliseconds(actualDuration.TotalMilliseconds * sample / curRate) : TimeSpan.FromMilliseconds(99999999); //lgtm[cs/loss-of-precision]
                        answer = string.Format(CultureInfo.InvariantCulture, "{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                                                t.Hours,
                                                t.Minutes,
                                                t.Seconds,
                                                t.Milliseconds);
                        Log.Debug("Flushing {0} results. ({1}/{4}s {2:0.00}/s overall {3} ETA)", sample, curRate, totRate * 1000, answer, actualDuration);
                    }
                    catch (Exception e) when (
                        e is OverflowException)
                    {
                        Log.Debug($"Overflowed: {curRate} {totRate} {sample} {t} {answer}");
                        Log.Debug("Flushing {0} results. ({1}/s {2:0.00}/s)", sample, curRate, totRate * 1000);
                    }

                    then = now;
                    prevFlush = sample;
                }
            }

            StopWatch.Stop();
            t = TimeSpan.FromMilliseconds(StopWatch.ElapsedMilliseconds);
            answer = string.Format(CultureInfo.InvariantCulture, "{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                                    t.Hours,
                                    t.Minutes,
                                    t.Seconds,
                                    t.Milliseconds);
            Log.Debug("Completed flushing in {0}", answer);
        }

        public static void ClearCollectors()
        {
            collectors = new List<BaseCollector>();
        }

        public static void ClearMonitors()
        {
            collectors = new List<BaseCollector>();
        }

        // Used for monitors. This writes a little spinner animation to indicate that monitoring is underway
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "These symbols won't be localized")]
        private static void WriteSpinner(ManualResetEvent untilDone)
        {
            int counter = 0;
            while (!untilDone.WaitOne(200))
            {
                counter++;
                switch (counter % 4)
                {
                    case 0: Console.Write("/"); break;
                    case 1: Console.Write("-"); break;
                    case 2: Console.Write("\\"); break;
                    case 3: Console.Write("|"); break;
                }
                if (Console.CursorLeft > 0)
                {
                    try
                    {
                        Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        Console.SetCursorPosition(0, Console.CursorTop);
                    }
                }
            }
        }
    }
}