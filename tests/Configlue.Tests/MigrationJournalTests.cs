using Configlue.Testing;

namespace Configlue.Tests;

public sealed class MigrationJournalTests
{
    [Test]
    public async Task MigrationRunHoldsOptionalJournalLeaseUntilCompletion()
    {
        var source = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) }
        );
        var target = new InMemoryStateStore<AppSettings.Fragment>();
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("source", source, priority: 100),
                new("target", target, priority: 0, writer: target),
            ])
        );
        var migration = new StateStorageMigrationDefinition<AppSettings.Fragment>(
            "migration",
            ["source"],
            [new StateStorageMigrationTarget<AppSettings.Fragment>("target", fragment => fragment)]
        );
        var journal = new TrackingMigrationJournal();

        await options.MigrateAsync(migration, journal);

        (journal.Acquired).ShouldBeTrue();
        (journal.Released).ShouldBeTrue();
        ((await target.ReadAsync()).Value!.RetryCount.Value).ShouldBe(12);
    }

    [Test]
    public async Task FileJournalLeaseSerializesSameMigrationAndHonorsCancellation()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );
        var lockDirectory = Path.Combine(directory, "locks");
        var options = new FileResourceOptions { LockDirectory = lockDirectory };
        var firstJournal = new FileStateStorageMigrationJournal(directory, options);
        var secondJournal = new FileStateStorageMigrationJournal(directory, options);
        Directory.CreateDirectory(directory);

        try
        {
            using var firstLease = await firstJournal.AcquireMigrationLeaseAsync("migration");
            await firstJournal.WriteAsync(
                new StateStorageMigrationProgress("migration", ["source"], ["target"])
            );

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await secondJournal.AcquireMigrationLeaseAsync("migration", cancellation.Token)
            );

            firstLease.Dispose();
            using var secondLease = await secondJournal.AcquireMigrationLeaseAsync("migration");
            (await secondJournal.ReadAsync("migration")).ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task FileJournalLeaseWaitsForInterprocessLockAndHonorsCancellation()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );
        var lockDirectory = Path.Combine(directory, "locks");
        var options = new FileResourceOptions { LockDirectory = lockDirectory };
        var journal = new FileStateStorageMigrationJournal(directory, options);
        Directory.CreateDirectory(directory);

        try
        {
            using (await journal.AcquireMigrationLeaseAsync("interprocess-migration")) { }
            var lockPath = Directory.EnumerateFiles(lockDirectory, "*.configlue.lock").Single();

            using (
                new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                )
            )
            {
                using var cancellation = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(100)
                );
                await Should.ThrowAsync<OperationCanceledException>(async () =>
                    await journal.AcquireMigrationLeaseAsync(
                        "interprocess-migration",
                        cancellation.Token
                    )
                );
            }

            using var lease = await journal.AcquireMigrationLeaseAsync("interprocess-migration");
            (lease).ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TrackingMigrationJournal
        : IStateStorageMigrationJournal,
            IStateStorageMigrationLeaseProvider
    {
        public bool Acquired { get; private set; }

        public bool Released { get; private set; }

        public ValueTask<StateStorageMigrationProgress?> ReadAsync(
            string migrationId,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<StateStorageMigrationProgress?>(null);

        public ValueTask WriteAsync(
            StateStorageMigrationProgress progress,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;

        public ValueTask<IDisposable> AcquireMigrationLeaseAsync(
            string migrationId,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Acquired = true;
            return ValueTask.FromResult<IDisposable>(new Lease(this));
        }

        private sealed class Lease(TrackingMigrationJournal owner) : IDisposable
        {
            public void Dispose() => owner.Released = true;
        }
    }
}
