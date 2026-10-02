using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Configlue;

namespace Configlue.Tests;

[ConfiglueModel("shared-mutable-collection-clone")]
public partial class SharedMutableCollectionRoot
{
    public Queue<SharedMutableCollectionRoot> Queue { get; set; } = new();
    public Queue<SharedMutableCollectionRoot> QueueAlias { get; set; } = new();
    public Stack<SharedMutableCollectionRoot> Stack { get; set; } = new();
    public Stack<SharedMutableCollectionRoot> StackAlias { get; set; } = new();
    public ConcurrentQueue<SharedMutableCollectionRoot> ConcurrentQueue { get; set; } = new();
    public ConcurrentQueue<SharedMutableCollectionRoot> ConcurrentQueueAlias { get; set; } = new();
    public ConcurrentStack<SharedMutableCollectionRoot> ConcurrentStack { get; set; } = new();
    public ConcurrentStack<SharedMutableCollectionRoot> ConcurrentStackAlias { get; set; } = new();
    public BlockingCollection<SharedMutableCollectionRoot> BlockingCollection { get; set; } =
        new(2);
    public BlockingCollection<SharedMutableCollectionRoot> BlockingCollectionAlias { get; set; } =
        new(2);
    public PriorityQueue<SharedMutableCollectionRoot, int> PriorityQueue { get; set; } = new();
    public PriorityQueue<SharedMutableCollectionRoot, int> PriorityQueueAlias { get; set; } = new();
    public LinkedList<SharedMutableCollectionRoot> LinkedList { get; set; } = new();
    public LinkedList<SharedMutableCollectionRoot> LinkedListAlias { get; set; } = new();
    public ObservableCollection<SharedMutableCollectionRoot> ObservableCollection { get; set; } =
        new();
    public ObservableCollection<SharedMutableCollectionRoot> ObservableCollectionAlias { get; set; } =
        new();
    public ReadOnlyCollection<SharedMutableCollectionRoot> ReadOnlyCollection { get; set; } =
        new(new List<SharedMutableCollectionRoot>());
    public ReadOnlyCollection<SharedMutableCollectionRoot> ReadOnlyCollectionAlias { get; set; } =
        new(new List<SharedMutableCollectionRoot>());
}

public sealed class SharedMutableCollectionCloneTests
{
    [Test]
    public void ClonePreservesAliasesAndCyclesForMutableCollections()
    {
        var root = new SharedMutableCollectionRoot();
        var comparer = Comparer<int>.Create(static (left, right) => right.CompareTo(left));
        var queue = new Queue<SharedMutableCollectionRoot>([root]);
        var stack = new Stack<SharedMutableCollectionRoot>([root]);
        var concurrentQueue = new ConcurrentQueue<SharedMutableCollectionRoot>([root]);
        var concurrentStack = new ConcurrentStack<SharedMutableCollectionRoot>([root]);
        var blockingCollection = new BlockingCollection<SharedMutableCollectionRoot>(2);
        blockingCollection.Add(root);
        var priorityQueue = new PriorityQueue<SharedMutableCollectionRoot, int>(comparer);
        priorityQueue.Enqueue(root, 5);
        var linkedList = new LinkedList<SharedMutableCollectionRoot>([root]);
        var observableCollection = new ObservableCollection<SharedMutableCollectionRoot>([root]);
        var readOnlyCollection = new ReadOnlyCollection<SharedMutableCollectionRoot>([root]);
        root.Queue = root.QueueAlias = queue;
        root.Stack = root.StackAlias = stack;
        root.ConcurrentQueue = root.ConcurrentQueueAlias = concurrentQueue;
        root.ConcurrentStack = root.ConcurrentStackAlias = concurrentStack;
        root.BlockingCollection = root.BlockingCollectionAlias = blockingCollection;
        root.PriorityQueue = root.PriorityQueueAlias = priorityQueue;
        root.LinkedList = root.LinkedListAlias = linkedList;
        root.ObservableCollection = root.ObservableCollectionAlias = observableCollection;
        root.ReadOnlyCollection = root.ReadOnlyCollectionAlias = readOnlyCollection;

        var clone = root.DeepClone();

        ReferenceEquals(clone.Queue, clone.QueueAlias).ShouldBeTrue();
        ReferenceEquals(clone.Queue, queue).ShouldBeFalse();
        ReferenceEquals(clone.Queue.Peek(), clone).ShouldBeTrue();
        ReferenceEquals(clone.Stack, clone.StackAlias).ShouldBeTrue();
        ReferenceEquals(clone.Stack, stack).ShouldBeFalse();
        ReferenceEquals(clone.Stack.Peek(), clone).ShouldBeTrue();
        ReferenceEquals(clone.ConcurrentQueue, clone.ConcurrentQueueAlias).ShouldBeTrue();
        ReferenceEquals(clone.ConcurrentQueue, concurrentQueue).ShouldBeFalse();
        ReferenceEquals(clone.ConcurrentQueue.Single(), clone).ShouldBeTrue();
        ReferenceEquals(clone.ConcurrentStack, clone.ConcurrentStackAlias).ShouldBeTrue();
        ReferenceEquals(clone.ConcurrentStack, concurrentStack).ShouldBeFalse();
        ReferenceEquals(clone.ConcurrentStack.Single(), clone).ShouldBeTrue();
        ReferenceEquals(clone.BlockingCollection, clone.BlockingCollectionAlias).ShouldBeTrue();
        ReferenceEquals(clone.BlockingCollection, blockingCollection).ShouldBeFalse();
        ReferenceEquals(clone.BlockingCollection.Single(), clone).ShouldBeTrue();
        ReferenceEquals(clone.PriorityQueue, clone.PriorityQueueAlias).ShouldBeTrue();
        ReferenceEquals(clone.PriorityQueue, priorityQueue).ShouldBeFalse();
        ReferenceEquals(clone.PriorityQueue.Dequeue(), clone).ShouldBeTrue();
        ReferenceEquals(clone.PriorityQueue.Comparer, comparer).ShouldBeTrue();
        ReferenceEquals(clone.LinkedList, clone.LinkedListAlias).ShouldBeTrue();
        ReferenceEquals(clone.LinkedList, linkedList).ShouldBeFalse();
        ReferenceEquals(clone.LinkedList.Single(), clone).ShouldBeTrue();
        ReferenceEquals(clone.ObservableCollection, clone.ObservableCollectionAlias).ShouldBeTrue();
        ReferenceEquals(clone.ObservableCollection, observableCollection).ShouldBeFalse();
        ReferenceEquals(clone.ObservableCollection.Single(), clone).ShouldBeTrue();
        ReferenceEquals(clone.ReadOnlyCollection, clone.ReadOnlyCollectionAlias).ShouldBeTrue();
        ReferenceEquals(clone.ReadOnlyCollection, readOnlyCollection).ShouldBeFalse();
        ReferenceEquals(clone.ReadOnlyCollection.Single(), clone).ShouldBeTrue();
        clone.BlockingCollection.BoundedCapacity.ShouldBe(2);
    }
}
