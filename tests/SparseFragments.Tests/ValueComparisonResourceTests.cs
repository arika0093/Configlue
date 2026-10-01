using System.Collections;

namespace SparseFragments.Tests;

public sealed class ValueComparisonResourceTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public void ComparisonDisposesEnumeratorsOnEqualityMismatchAndFailure(bool mismatch, bool fail)
    {
        var left = new TrackedSequence(1, false);
        var right = new TrackedSequence(mismatch ? 2 : 1, fail);

        if (fail)
            Should.Throw<InvalidOperationException>(() =>
                SparseValueComparer.AreEqual(left, right)
            );
        else
            SparseValueComparer.AreEqual(left, right).ShouldBe(!mismatch);

        left.Iterator.Disposed.ShouldBeTrue();
        right.Iterator.Disposed.ShouldBeTrue();
    }

    [Test]
    public void LeftEnumeratorIsDisposedWhenRightAcquisitionFails()
    {
        var left = new TrackedSequence(1, false);

        Should.Throw<InvalidOperationException>(() =>
            SparseValueComparer.AreEqual(left, new AcquisitionFailure())
        );

        left.Iterator.Disposed.ShouldBeTrue();
    }

    private sealed class AcquisitionFailure : IEnumerable
    {
        public IEnumerator GetEnumerator() => throw new InvalidOperationException("acquisition");
    }

    private sealed class TrackedSequence(int value, bool fail) : IEnumerable
    {
        public TrackedEnumerator Iterator { get; } = new(value, fail);

        public IEnumerator GetEnumerator() => Iterator;
    }

    private sealed class TrackedEnumerator(int value, bool fail) : IEnumerator, IDisposable
    {
        private bool _moved;
        public bool Disposed { get; private set; }
        public object Current => value;

        public bool MoveNext()
        {
            if (fail)
                throw new InvalidOperationException("enumeration");
            if (_moved)
                return false;
            _moved = true;
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }
}
