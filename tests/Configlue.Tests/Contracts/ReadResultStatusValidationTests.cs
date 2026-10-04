using Configlue.State;

namespace Configlue.Tests;

public sealed class ReadResultStatusValidationTests
{
    [Test]
    [Arguments(-1)]
    [Arguments(4)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public void InternalResultCreationRejectsUndefinedStatuses(int status)
    {
        var exception = Should.Throw<ArgumentOutOfRangeException>(() =>
            StateReadResult<int>.Create((StateReadStatus)status, 42)
        );
        exception.ParamName.ShouldBe("Status");
    }
}
