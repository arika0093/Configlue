namespace Configlue.DevTools.Tests;

public sealed class EditorThrottleTests
{
    [Test]
    public void RapidKeystrokes_DoNotSync()
    {
        var throttle = new ConfiglueDevToolsDraftThrottle(TimeSpan.FromMilliseconds(600));
        var start = DateTimeOffset.UtcNow;

        throttle.NoteEdit(start);
        throttle.ShouldSync(start.AddMilliseconds(100)).ShouldBeFalse();

        // A burst of keystrokes keeps postponing the quiet window.
        throttle.NoteEdit(start.AddMilliseconds(200));
        throttle.NoteEdit(start.AddMilliseconds(300));
        throttle.ShouldSync(start.AddMilliseconds(400)).ShouldBeFalse();
        throttle.HasPendingEdit.ShouldBeTrue();
    }

    [Test]
    public void QuietDraft_SyncsAfterDebounceInterval()
    {
        var throttle = new ConfiglueDevToolsDraftThrottle(TimeSpan.FromMilliseconds(600));
        var start = DateTimeOffset.UtcNow;

        throttle.NoteEdit(start);
        throttle.ShouldSync(start.AddMilliseconds(599)).ShouldBeFalse();
        throttle.ShouldSync(start.AddMilliseconds(600)).ShouldBeTrue();

        throttle.MarkSent(start.AddMilliseconds(600));
        throttle.ShouldSync(start.AddMilliseconds(3600)).ShouldBeFalse();
        throttle.HasPendingEdit.ShouldBeFalse();
    }

    [Test]
    public void ExplicitRequest_AlwaysSyncs()
    {
        var throttle = new ConfiglueDevToolsDraftThrottle(TimeSpan.FromMilliseconds(600));
        var start = DateTimeOffset.UtcNow;

        // No edits at all: explicit Save/Diff/Validate still synchronizes.
        throttle.ShouldSync(start, explicitRequest: true).ShouldBeTrue();

        throttle.NoteEdit(start);
        throttle.ShouldSync(start, explicitRequest: true).ShouldBeTrue();
        throttle.ShouldSync(start.AddMilliseconds(1), explicitRequest: true).ShouldBeTrue();
    }

    [Test]
    public void NoEdits_NeverBackgroundSyncs()
    {
        var throttle = new ConfiglueDevToolsDraftThrottle(TimeSpan.FromMilliseconds(100));
        var start = DateTimeOffset.UtcNow;

        throttle.ShouldSync(start).ShouldBeFalse();
        throttle.ShouldSync(start.AddHours(1)).ShouldBeFalse();
        throttle.HasPendingEdit.ShouldBeFalse();
    }

    [Test]
    public void NegativeDebounce_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new ConfiglueDevToolsDraftThrottle(TimeSpan.FromMilliseconds(-1))
        );
    }
}
