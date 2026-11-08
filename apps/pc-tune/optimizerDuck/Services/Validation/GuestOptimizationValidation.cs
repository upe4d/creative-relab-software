using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Conditions;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Conditions;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.History;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Revert;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Validation;

/// <summary>Реальные операции и откат разрешены только в закреплённой лабораторной Windows.</summary>
internal static class GuestOptimizationValidation
{
    internal static async Task<int> RunAsync()
    {
        // Проверка до создания каталогов и разрешения любого изменения Windows.
        try
        {
            using var query = new ManagementObjectSearcher(
                "SELECT UUID FROM Win32_ComputerSystemProduct"
            );
            using var rows = query.Get();
            var uuid = rows.Cast<ManagementObject>()
                .Select(row => Convert.ToString(row["UUID"], CultureInfo.InvariantCulture))
                .Single();
            if (
                !string.Equals(
                    uuid,
                    "dbcd7a01-f5fe-4ab7-a61d-a30cf8d45607",
                    StringComparison.OrdinalIgnoreCase
                )
                || Environment.MachineName != "PCGUARD-LAB"
                || !WindowsIdentity
                    .GetCurrent()
                    .Name.EndsWith(@"\PCTuneTester", StringComparison.OrdinalIgnoreCase)
                || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(
                    WindowsBuiltInRole.Administrator
                )
            )
                return 100;
        }
        catch
        {
            return 100;
        }
        Shared.ValidatedLabDataDirectory = @"C:\PCTuneLab\Data";
        var directory = Path.Combine(Shared.RootDirectory, "Runtime");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(Shared.RootDirectory, "Temp"));
        Environment.SetEnvironmentVariable("TEMP", Path.Combine(Shared.RootDirectory, "Temp"));
        Environment.SetEnvironmentVariable("TMP", Path.Combine(Shared.RootDirectory, "Temp"));
        var results = new List<object>();
        string? fatal = null;
        object? system = null;
        var protectionBefore = GuestStateSnapshot.Protection();
        try
        {
            Loc.Instance.ChangeCulture(CultureInfo.GetCultureInfo("ru-RU"));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Optimize:ShellTimeoutMs"] = "60000",
                        ["App:Language"] = "ru-RU",
                    }
                )
                .Build();
            var collection = new ServiceCollection();
            collection.AddLogging(builder =>
                builder.AddProvider(new LabFileLogger(Path.Combine(directory, "operations.log")))
            );
            collection.AddOptimizerApplication(configuration);
            using var services = collection.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
            );
            var info = services.GetRequiredService<SystemInfoService>();
            var snapshot = await info.EnsureSnapshotAsync();
            system = new
            {
                snapshot.IsUnknown,
                identity = WindowsIdentity.GetCurrent().Name,
                build = Environment.OSVersion.VersionString,
                sid = WindowsIdentity.GetCurrent().User?.Value,
                assemblySHA256 = Convert.ToHexString(
                    global::System.Security.Cryptography.SHA256.HashData(
                        File.ReadAllBytes(typeof(App).Assembly.Location)
                    )
                ),
            };
            if (snapshot.IsUnknown)
                throw new InvalidOperationException("Не получен снимок Windows и оборудования");
            var runner = services.GetRequiredService<OptimizationService>();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Lab");
            var index = JObject.Parse(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-index.json"))
            );
            var types = typeof(App)
                .Assembly.GetTypes()
                .Where(type => type.GetCustomAttribute<OptimizationAttribute>() != null)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
            if (types.Length != 42)
                throw new InvalidOperationException("Неверное число оптимизаций");
            foreach (var type in types)
            {
                var item = (BaseOptimization)Activator.CreateInstance(type)!;
                item.OwnerType = type.DeclaringType;
                var meta = type.GetCustomAttribute<OptimizationAttribute>()!;
                var compatibility = ConditionEvaluator.Evaluate(meta.Condition, snapshot, logger);
                var scope = (JObject)index[type.Name]!;
                var start = DateTimeOffset.Now;
                if (compatibility.IsBlocking)
                {
                    results.Add(
                        new
                        {
                            key = item.OptimizationKey,
                            name = item.Name,
                            status = "UNSUPPORTED",
                            reason = compatibility.Description,
                            started = start,
                            revert = "not_applicable",
                        }
                    );
                    SaveProgress(results, system, directory);
                    continue;
                }
                var before = GuestStateSnapshot.Capture(scope, type.Name);
                var apply = await runner.ApplyAsync(item, new Progress<ProcessingProgress>());
                var after = GuestStateSnapshot.Capture(scope, type.Name);
                var record = ChangeRecordStore.TryRead(item.Id, logger);
                var revertData = await RevertManager.GetRevertDataAsync(item.Id);
                if (record != null)
                    File.WriteAllText(
                        Path.Combine(directory, type.Name + "-apply.json"),
                        JsonConvert.SerializeObject(record, Formatting.Indented)
                    );
                if (revertData != null)
                    File.WriteAllText(
                        Path.Combine(directory, type.Name + "-revert-data.json"),
                        JsonConvert.SerializeObject(revertData, Formatting.Indented)
                    );
                var revert = revertData == null ? null : await runner.RevertAsync(item);
                var restored = GuestStateSnapshot.Capture(scope, type.Name);
                var appliedDelta = GuestStateSnapshot.Difference(before, after);
                var remainingDelta = GuestStateSnapshot.Difference(before, restored);
                var failedSteps = record?.Steps.Where(step => !step.Ok).ToArray();
                var noObservations = before.Count == 0;
                var readErrors = before
                    .Values.Concat(after.Values)
                    .Concat(restored.Values)
                    .Any(value => value.StartsWith("ERROR:", StringComparison.Ordinal));
                var notApplicable =
                    record is { Steps.Count: > 0 }
                    && record.Steps.All(step =>
                        step.Kind == optimizerDuck.Domain.Execution.ChangeKind.NotApplicable
                    );
                var status =
                    notApplicable ? "UNSUPPORTED"
                    : remainingDelta.Length != 0 || revert is { Success: false } ? "ROLLBACK_FAILED"
                    : failedSteps is { Length: > 0 } ? "PARTIAL_OR_REFUSED"
                    : appliedDelta.Length == 0 ? "NO_CHANGE"
                    : readErrors || noObservations ? "OBSERVATION_INCOMPLETE"
                    : "PASS";
                var entry = new
                {
                    key = item.OptimizationKey,
                    name = item.Name,
                    started = start,
                    finished = DateTimeOffset.Now,
                    status,
                    compatibility = compatibility.State.ToString(),
                    applyStatus = apply.Status.ToString(),
                    apply.Message,
                    applyDelta = appliedDelta,
                    rollbackDelta = remainingDelta,
                    reverted = revert?.Success,
                    revertMessage = revert?.Message,
                    revertFileWritten = revertData != null,
                    recordedSteps = record?.Steps.Count,
                    failedSteps,
                    readErrors,
                    observedEntries = before.Count,
                    before,
                    after,
                    restored,
                };
                File.WriteAllText(
                    Path.Combine(directory, type.Name + "-evidence.json"),
                    JsonConvert.SerializeObject(entry, Formatting.Indented)
                );
                results.Add(entry);
                SaveProgress(results, system, directory);
                var protectionNow = GuestStateSnapshot.Protection();
                if (GuestStateSnapshot.Difference(protectionBefore, protectionNow).Length != 0)
                    throw new InvalidOperationException(
                        "Изменилось состояние защищённых антивирусных служб"
                    );
            }
        }
        catch (Exception ex)
        {
            fatal = ex.ToString();
        }
        var protectionAfter = GuestStateSnapshot.Protection();
        var report = new
        {
            at = DateTimeOffset.Now,
            completed = results.Count == 42 && fatal == null,
            total = results.Count,
            system,
            fatal,
            protectionBefore,
            protectionAfter,
            protectionUnchanged = GuestStateSnapshot
                .Difference(protectionBefore, protectionAfter)
                .Length == 0,
            results,
        };
        var json = JsonConvert.SerializeObject(report, Formatting.Indented);
        File.WriteAllText(Path.Combine(directory, "results.json"), json);
        File.WriteAllText(@"C:\PCGuardLab\Reports\kernel-results.json", json);
        File.WriteAllText(@"C:\PCGuardLab\Reports\tune-results.json", json);
        return fatal == null && results.Count == 42 ? 0 : 1;
    }

    private static void SaveProgress(List<object> results, object? system, string directory)
    {
        var json = JsonConvert.SerializeObject(
            new
            {
                completed = false,
                total = results.Count,
                system,
                results,
            },
            Formatting.Indented
        );
        File.WriteAllText(Path.Combine(directory, "progress.json"), json);
        File.WriteAllText(@"C:\PCGuardLab\Reports\tune-progress.json", json);
    }

    private sealed class LabFileLogger(string path) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new LineLogger(path, categoryName);

        public void Dispose() { }
    }

    private sealed class LineLogger(string path, string category) : ILogger
    {
        private static readonly global::System.Threading.Lock Sync = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

        public void Log<TState>(
            LogLevel level,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(level))
                return;
            lock (Sync)
                File.AppendAllText(
                    path,
                    DateTimeOffset.Now.ToString("o")
                        + " "
                        + category
                        + " "
                        + formatter(state, exception)
                        + " "
                        + exception
                        + Environment.NewLine
                );
        }
    }
}
