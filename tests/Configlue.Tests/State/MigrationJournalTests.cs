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
                new StateSource<AppSettings.Fragment>("source", source, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                new StateSource<AppSettings.Fragment>("target", target, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = target }),
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
                new StateSource<AppSettings.Fragment>("source", source, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                new StateSource<AppSettings.Fragment>("target", target, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = target }),
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
        var firstJournal = new FileStateStorageMigrationJournal(directory);
        var secondJournal = new FileStateStorageMigrationJournal(directory);
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
    public async Task CompletedJournalReappliesRetirementOnSecondCall()
    {
        var source = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) }
        );
        var target = new InMemoryStateSource<AppSettings.Fragment>();
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("source", source, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                new StateSource<AppSettings.Fragment>("target", target, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = target }),
            ]),
            defaultWritePlan: StateWritePlan.DefaultTo(SourceId.From("target"))
        );
        var migration = new StateStorageMigrationDefinition<AppSettings.Fragment>(
            "retire-reapply-same-state",
            [SourceId.From("source")],
            [new StateStorageMigrationTarget<AppSettings.Fragment>(SourceId.From("target"), fragment => fragment)],
            retireSources: true
        );
        var journal = new InMemoryMigrationJournal();

        var first = await runtime.MigrateAsync(migration, journal);
        (first.SourcesRetired).ShouldBeTrue();
        ((await target.ReadAsync()).Value!.RetryCount.Value).ShouldBe(12);
        var firstRead = await runtime.ReadAsync();
        (firstRead.Value!.RetryCount).ShouldBe(12);
        (firstRead.Revisions!.TryGetRevision(SourceId.From("source"), out _)).ShouldBeFalse();

        var second = await runtime.MigrateAsync(migration, journal);
        (second.SourcesRetired).ShouldBeTrue();
        ((await target.ReadAsync()).Value!.RetryCount.Value).ShouldBe(12);
        var secondRead = await runtime.ReadAsync();
        (secondRead.Value!.RetryCount).ShouldBe(12);
        (secondRead.Revisions!.TryGetRevision(SourceId.From("source"), out _)).ShouldBeFalse();

        await runtime.SaveAsync(settings => settings.RetryCount = 13);
        ((await target.ReadAsync()).Value!.RetryCount.Value).ShouldBe(13);
        ((await source.ReadAsync()).Value!.RetryCount.Value).ShouldBe(12);
        ((await runtime.ReadAsync()).Value!.RetryCount).ShouldBe(13);
    }

    [Test]
    public async Task CompletedJournalRetiresSourcesOnRecreatedState()
    {
        var source = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) }
        );
        var target = new InMemoryStateSource<AppSettings.Fragment>();
        var journal = new InMemoryMigrationJournal();
        var migration = new StateStorageMigrationDefinition<AppSettings.Fragment>(
            "retire-reapply-restart",
            [SourceId.From("source")],
            [new StateStorageMigrationTarget<AppSettings.Fragment>(SourceId.From("target"), fragment => fragment)],
            retireSources: true
        );

        await using (var firstState = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("source", source, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                new StateSource<AppSettings.Fragment>("target", target, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = target }),
            ]),
            defaultWritePlan: StateWritePlan.DefaultTo(SourceId.From("target"))
        ))
        {
            var first = await firstState.MigrateAsync(migration, journal);
            (first.SourcesRetired).ShouldBeTrue();
            ((await target.ReadAsync()).Value!.RetryCount.Value).ShouldBe(12);
        }

        await using var secondState = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("source", source, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                new StateSource<AppSettings.Fragment>("target", target, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = target }),
            ]),
            defaultWritePlan: StateWritePlan.DefaultTo(SourceId.From("target"))
        );

        var before = await secondState.ReadAsync();
        (before.Revisions!.TryGetRevision(SourceId.From("source"), out _)).ShouldBeTrue();

        var second = await secondState.MigrateAsync(migration, journal);
        (second.SourcesRetired).ShouldBeTrue();

        var after = await secondState.ReadAsync();
        (after.Revisions!.TryGetRevision(SourceId.From("source"), out _)).ShouldBeFalse();
        (after.Value!.RetryCount).ShouldBe(12);
        ((await target.ReadAsync()).Value!.RetryCount.Value).ShouldBe(12);
    }

    [Test]
    public async Task CompletedJournalRejectsMismatchedDefinition()
    {
        var source = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) }
        );
        var target = new InMemoryStateSource<AppSettings.Fragment>();
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("source", source, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                new StateSource<AppSettings.Fragment>("target", target, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = target }),
            ])
        );
        var completed = new StateStorageMigrationDefinition<AppSettings.Fragment>(
            "retire-reapply-mismatch",
            [SourceId.From("source")],
            [new StateStorageMigrationTarget<AppSettings.Fragment>(SourceId.From("target"), fragment => fragment)],
            retireSources: true
        );
        var journal = new InMemoryMigrationJournal();

        var progress = await runtime.MigrateAsync(completed, journal);
        (progress.SourcesRetired).ShouldBeTrue();

        var mismatched = new StateStorageMigrationDefinition<AppSettings.Fragment>(
            "retire-reapply-mismatch",
            [SourceId.From("other")],
            [new StateStorageMigrationTarget<AppSettings.Fragment>(SourceId.From("target"), fragment => fragment)],
            retireSources: true
        );
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await runtime.MigrateAsync(mismatched, journal)
        );
    }

    private sealed class InMemoryMigrationJournal : IStateStorageMigrationJournal
    {
        private readonly Dictionary<string, StateStorageMigrationProgress> _stored = new();

        public ValueTask<StateStorageMigrationProgress?> ReadAsync(
            string migrationId,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stored.TryGetValue(migrationId, out var progress);
            return ValueTaskCompat.FromResult<StateStorageMigrationProgress?>(progress);
        }

        public ValueTask WriteAsync(
            StateStorageMigrationProgress progress,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(progress);
            cancellationToken.ThrowIfCancellationRequested();
            _stored[progress.MigrationId] = progress;
            return ValueTask.CompletedTask;
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
                : ValueTaskCompat.FromException<StateStorageMigrationProgress?>(ReadFailure);
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
                : ValueTaskCompat.FromException(WriteFailure);
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
