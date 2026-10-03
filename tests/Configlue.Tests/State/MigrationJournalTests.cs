using Configlue.Testing;

namespace Configlue.Tests;

public sealed class MigrationJournalTests
{
    [Test]
    public async Task MigrationRunHoldsOptionalJournalLeaseUntilCompletion()
    {
        var source = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) }
        );
        var target = new InMemoryStateSource<AppSettings.Fragment>();
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("source", source, priority: 100),
                new("target", target, priority: 0, writer: target),
            ])
        );
        var migration = new StateStorageMigrationDefinition<AppSettings.Fragment>(
            "migration",
            [SourceId.From("source")],
            [new StateStorageMigrationTarget<AppSettings.Fragment>(SourceId.From("target"), fragment => fragment)]
        );
        var journal = new TrackingMigrationJournal();

        await options.MigrateAsync(migration, journal);

        (journal.Acquired).ShouldBeTrue();
        (journal.Released).ShouldBeTrue();
        ((await target.ReadAsync()).Value!.RetryCount.Value).ShouldBe(12);
    }

    [Test]
    public async Task MigrationAwaitsLeaseReleaseAndReleasesAfterJournalFailure()
    {
        var source = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) }
        );
        var target = new InMemoryStateSource<AppSettings.Fragment>();
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("source", source, priority: 100),
                new("target", target, priority: 0, writer: target),
            ])
        );
        var migration = new StateStorageMigrationDefinition<AppSettings.Fragment>(
            "migration-await-release",
            [SourceId.From("source")],
            [new StateStorageMigrationTarget<AppSettings.Fragment>(SourceId.From("target"), fragment => fragment)]
        );
        var journal = new TrackingMigrationJournal
        {
            ReleaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        var migrationTask = runtime.MigrateAsync(migration, journal).AsTask();
        await journal.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        migrationTask.IsCompleted.ShouldBeFalse();
        journal.ReleaseGate.TrySetResult();
        await migrationTask;
        journal.Released.ShouldBeTrue();

        var failingJournal = new TrackingMigrationJournal
        {
            ReadFailure = new IOException("journal read failed"),
        };
        await Should.ThrowAsync<IOException>(async () =>
            await runtime.MigrateAsync(migration, failingJournal)
        );
        failingJournal.Released.ShouldBeTrue();

        var cancelingJournal = new TrackingMigrationJournal
        {
            ReadFailure = new OperationCanceledException(),
        };
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await runtime.MigrateAsync(migration, cancelingJournal)
        );
        cancelingJournal.Released.ShouldBeTrue();

        var failingWriteJournal = new TrackingMigrationJournal
        {
            WriteFailure = new IOException("journal write failed"),
        };
        await Should.ThrowAsync<IOException>(async () =>
            await runtime.MigrateAsync(migration, failingWriteJournal)
        );
        failingWriteJournal.Released.ShouldBeTrue();

        var releaseFailureJournal = new TrackingMigrationJournal
        {
            ReleaseFailure = new IOException("lease release failed"),
        };
        await Should.ThrowAsync<IOException>(async () =>
            await runtime.MigrateAsync(migration, releaseFailureJournal)
        );
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
            await using var firstLease = await firstJournal.AcquireMigrationLeaseAsync("migration");
            await firstJournal.WriteAsync(
                new StateStorageMigrationProgress(
                    "migration",
                    [SourceId.From("source")],
                    [SourceId.From("target")]
                )
            );

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await secondJournal.AcquireMigrationLeaseAsync("migration", cancellation.Token)
            );

            await firstLease.DisposeAsync();
            await using var secondLease = await secondJournal.AcquireMigrationLeaseAsync("migration");
            var progress = await secondJournal.ReadAsync("migration");
            (progress).ShouldNotBeNull();
            progress!.SourceIds.ShouldBe([SourceId.From("source")]);
            progress.TargetSourceIds.ShouldBe([SourceId.From("target")]);
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
            await using (await journal.AcquireMigrationLeaseAsync("interprocess-migration")) { }
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

            await using var lease = await journal.AcquireMigrationLeaseAsync("interprocess-migration");
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

        public TaskCompletionSource ReleaseStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource? ReleaseGate { get; init; }

        public Exception? ReadFailure { get; init; }

        public Exception? WriteFailure { get; init; }

        public Exception? ReleaseFailure { get; init; }

        public ValueTask<StateStorageMigrationProgress?> ReadAsync(
            string migrationId,
            CancellationToken cancellationToken = default
        )
        {
            _ = migrationId;
            cancellationToken.ThrowIfCancellationRequested();
            return ReadFailure is null
                ? ValueTaskCompat.FromResult<StateStorageMigrationProgress?>(null)
                : ValueTask.FromException<StateStorageMigrationProgress?>(ReadFailure);
        }

        public ValueTask WriteAsync(
            StateStorageMigrationProgress progress,
            CancellationToken cancellationToken = default
        )
        {
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            return WriteFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(WriteFailure);
        }

        public ValueTask<IAsyncDisposable> AcquireMigrationLeaseAsync(
            string migrationId,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Acquired = true;
            return ValueTaskCompat.FromResult<IAsyncDisposable>(new Lease(this));
        }

        private sealed class Lease(TrackingMigrationJournal owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                return ReleaseAsync();

                async ValueTask ReleaseAsync()
                {
                    owner.ReleaseStarted.TrySetResult();
                    if (owner.ReleaseGate is { } gate)
                    {
                        await gate.Task.ConfigureAwait(false);
                    }
                    owner.Released = true;
                    if (owner.ReleaseFailure is { } failure)
                    {
                        throw failure;
                    }
                }
            }
        }
    }
}
