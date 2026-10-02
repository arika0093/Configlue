using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Configlue;

namespace Configlue.Tests;

[ConfiglueModel("shared-mutable-collection-clone")]
public partial class SharedMutableCollectionRoot
{
    public int Value { get; set; }
    public IReadOnlyCollection<SharedMutableCollectionRoot> AImmutableListView { get; set; } =
        Array.Empty<SharedMutableCollectionRoot>();
    public IReadOnlyCollection<SharedMutableCollectionRoot> AImmutableSetView { get; set; } =
        Array.Empty<SharedMutableCollectionRoot>();
    public IReadOnlyDictionary<
        string,
        SharedMutableCollectionRoot
    > AImmutableDictionaryView { get; set; } =
        new Dictionary<string, SharedMutableCollectionRoot>();
    public IReadOnlyCollection<SharedMutableCollectionRoot> AQueueView { get; set; } =
        Array.Empty<SharedMutableCollectionRoot>();
    public IReadOnlyCollection<SharedMutableCollectionRoot> ASetView { get; set; } =
        Array.Empty<SharedMutableCollectionRoot>();
    public Queue<SharedMutableCollectionRoot> Queue { get; set; } = new();
    public Queue<SharedMutableCollectionRoot> QueueAlias { get; set; } = new();
    public Stack<SharedMutableCollectionRoot> Stack { get; set; } = new();
    public Stack<SharedMutableCollectionRoot> StackAlias { get; set; } = new();
    public HashSet<SharedMutableCollectionRoot> Set { get; set; } = new();
    public HashSet<SharedMutableCollectionRoot> SetAlias { get; set; } = new();
    public ImmutableList<SharedMutableCollectionRoot> ImmutableList { get; set; } =
        ImmutableList<SharedMutableCollectionRoot>.Empty;
    public ImmutableList<SharedMutableCollectionRoot> ImmutableListAlias { get; set; } =
        ImmutableList<SharedMutableCollectionRoot>.Empty;
    public ImmutableHashSet<SharedMutableCollectionRoot> ImmutableSet { get; set; } =
        ImmutableHashSet<SharedMutableCollectionRoot>.Empty;
    public ImmutableHashSet<SharedMutableCollectionRoot> ImmutableSetAlias { get; set; } =
        ImmutableHashSet<SharedMutableCollectionRoot>.Empty;
    public ImmutableDictionary<
        string,
        SharedMutableCollectionRoot
    > ImmutableDictionary { get; set; } =
        ImmutableDictionary<string, SharedMutableCollectionRoot>.Empty;
    public ImmutableDictionary<
        string,
        SharedMutableCollectionRoot
    > ImmutableDictionaryAlias { get; set; } =
        ImmutableDictionary<string, SharedMutableCollectionRoot>.Empty;
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
        var root = new SharedMutableCollectionRoot { Value = 1 };
        var other = new SharedMutableCollectionRoot { Value = 2 };
        var comparer = Comparer<int>.Create(static (left, right) => right.CompareTo(left));
        var queue = new Queue<SharedMutableCollectionRoot>([root, other]);
        var stack = new Stack<SharedMutableCollectionRoot>([root, other]);
        var set = new HashSet<SharedMutableCollectionRoot>([root, other]);
        var immutableList = ImmutableList.Create(root, other);
        IEqualityComparer<SharedMutableCollectionRoot> immutableSetComparer =
            ReferenceEqualityComparer.Instance;
        var immutableSet = ImmutableHashSet.Create(immutableSetComparer, root, other);
        var immutableDictionaryComparer = StringComparer.OrdinalIgnoreCase;
        IEqualityComparer<SharedMutableCollectionRoot> immutableDictionaryValueComparer =
            ReferenceEqualityComparer.Instance;
        var immutableDictionary = ImmutableDictionary
            .Create<string, SharedMutableCollectionRoot>(
                immutableDictionaryComparer,
                immutableDictionaryValueComparer
            )
            .Add("Key", root)
            .Add("Other", other);
        var concurrentQueue = new ConcurrentQueue<SharedMutableCollectionRoot>([root, other]);
        var concurrentStack = new ConcurrentStack<SharedMutableCollectionRoot>([root, other]);
        var blockingCollection = new BlockingCollection<SharedMutableCollectionRoot>(2);
        blockingCollection.Add(root);
        blockingCollection.Add(other);
        var priorityQueue = new PriorityQueue<SharedMutableCollectionRoot, int>(comparer);
        priorityQueue.Enqueue(root, 5);
        priorityQueue.Enqueue(other, 3);
        var linkedList = new LinkedList<SharedMutableCollectionRoot>([root, other]);
        var observableCollection = new ObservableCollection<SharedMutableCollectionRoot>([
            root,
            other,
        ]);
        var readOnlyCollection = new ReadOnlyCollection<SharedMutableCollectionRoot>([root, other]);
        root.AQueueView = queue;
        root.ASetView = set;
        root.AImmutableListView = immutableList;
        root.AImmutableSetView = immutableSet;
        root.AImmutableDictionaryView = immutableDictionary;
        root.Queue = root.QueueAlias = queue;
        root.Stack = root.StackAlias = stack;
        root.Set = root.SetAlias = set;
        root.ImmutableList = root.ImmutableListAlias = immutableList;
        root.ImmutableSet = root.ImmutableSetAlias = immutableSet;
        root.ImmutableDictionary = root.ImmutableDictionaryAlias = immutableDictionary;
        root.ConcurrentQueue = root.ConcurrentQueueAlias = concurrentQueue;
        root.ConcurrentStack = root.ConcurrentStackAlias = concurrentStack;
        root.BlockingCollection = root.BlockingCollectionAlias = blockingCollection;
        root.PriorityQueue = root.PriorityQueueAlias = priorityQueue;
        root.LinkedList = root.LinkedListAlias = linkedList;
        root.ObservableCollection = root.ObservableCollectionAlias = observableCollection;
        root.ReadOnlyCollection = root.ReadOnlyCollectionAlias = readOnlyCollection;

        var clone = root.DeepClone();

        ReferenceEquals(clone.Queue, clone.QueueAlias).ShouldBeTrue();
        ReferenceEquals(clone.AQueueView, clone.Queue).ShouldBeTrue();
        ReferenceEquals(clone.Queue, queue).ShouldBeFalse();
        ReferenceEquals(clone.Queue.Peek(), clone).ShouldBeTrue();
        var cloneOther = clone.Queue.ElementAt(1);
        cloneOther.Value.ShouldBe(2);
        ReferenceEquals(clone.Stack, clone.StackAlias).ShouldBeTrue();
        ReferenceEquals(clone.Stack, stack).ShouldBeFalse();
        ReferenceEquals(clone.Stack.Peek(), cloneOther).ShouldBeTrue();
        ReferenceEquals(clone.Stack.Last(), clone).ShouldBeTrue();
        ReferenceEquals(clone.Set, clone.SetAlias).ShouldBeTrue();
        ReferenceEquals(clone.ASetView, clone.Set).ShouldBeTrue();
        ReferenceEquals(clone.Set, set).ShouldBeFalse();
        clone.Set.Count.ShouldBe(2);
        ReferenceEquals(clone.ImmutableList, clone.ImmutableListAlias).ShouldBeTrue();
        ReferenceEquals(clone.AImmutableListView, clone.ImmutableList).ShouldBeTrue();
        ReferenceEquals(clone.ImmutableList, immutableList).ShouldBeFalse();
        ReferenceEquals(clone.ImmutableList[0], clone).ShouldBeTrue();
        ReferenceEquals(clone.ImmutableList[1], cloneOther).ShouldBeTrue();
        clone.ImmutableList.Count.ShouldBe(2);
        ReferenceEquals(clone.ImmutableSet, clone.ImmutableSetAlias).ShouldBeTrue();
        ReferenceEquals(clone.AImmutableSetView, clone.ImmutableSet).ShouldBeTrue();
        ReferenceEquals(clone.ImmutableSet, immutableSet).ShouldBeFalse();
        clone.ImmutableSet.Contains(clone).ShouldBeTrue();
        clone.ImmutableSet.Contains(cloneOther).ShouldBeTrue();
        clone.ImmutableSet.Count.ShouldBe(2);
        ReferenceEquals(clone.ImmutableSet.KeyComparer, immutableSetComparer).ShouldBeTrue();
        ReferenceEquals(clone.ImmutableDictionary, clone.ImmutableDictionaryAlias).ShouldBeTrue();
        ReferenceEquals(clone.AImmutableDictionaryView, clone.ImmutableDictionary).ShouldBeTrue();
        ReferenceEquals(clone.ImmutableDictionary, immutableDictionary).ShouldBeFalse();
        ReferenceEquals(clone.ImmutableDictionary["KEY"], clone).ShouldBeTrue();
        ReferenceEquals(clone.ImmutableDictionary["other"], cloneOther).ShouldBeTrue();
        ReferenceEquals(clone.ImmutableDictionary.KeyComparer, immutableDictionaryComparer)
            .ShouldBeTrue();
        ReferenceEquals(clone.ImmutableDictionary.ValueComparer, immutableDictionaryValueComparer)
            .ShouldBeTrue();
        ReferenceEquals(clone.ConcurrentQueue, clone.ConcurrentQueueAlias).ShouldBeTrue();
        ReferenceEquals(clone.ConcurrentQueue, concurrentQueue).ShouldBeFalse();
        ReferenceEquals(clone.ConcurrentQueue.First(), clone).ShouldBeTrue();
        ReferenceEquals(clone.ConcurrentQueue.Last(), cloneOther).ShouldBeTrue();
        ReferenceEquals(clone.ConcurrentStack, clone.ConcurrentStackAlias).ShouldBeTrue();
        ReferenceEquals(clone.ConcurrentStack, concurrentStack).ShouldBeFalse();
        ReferenceEquals(clone.ConcurrentStack.First(), cloneOther).ShouldBeTrue();
        ReferenceEquals(clone.ConcurrentStack.Last(), clone).ShouldBeTrue();
        ReferenceEquals(clone.BlockingCollection, clone.BlockingCollectionAlias).ShouldBeTrue();
        ReferenceEquals(clone.BlockingCollection, blockingCollection).ShouldBeFalse();
        ReferenceEquals(clone.BlockingCollection.First(), clone).ShouldBeTrue();
        ReferenceEquals(clone.BlockingCollection.Last(), cloneOther).ShouldBeTrue();
        ReferenceEquals(clone.PriorityQueue, clone.PriorityQueueAlias).ShouldBeTrue();
        ReferenceEquals(clone.PriorityQueue, priorityQueue).ShouldBeFalse();
        ReferenceEquals(clone.PriorityQueue.Dequeue(), clone).ShouldBeTrue();
        ReferenceEquals(clone.PriorityQueue.Dequeue(), cloneOther).ShouldBeTrue();
        ReferenceEquals(clone.PriorityQueue.Comparer, comparer).ShouldBeTrue();
        ReferenceEquals(clone.LinkedList, clone.LinkedListAlias).ShouldBeTrue();
        ReferenceEquals(clone.LinkedList, linkedList).ShouldBeFalse();
        ReferenceEquals(clone.LinkedList.First!.Value, clone).ShouldBeTrue();
        ReferenceEquals(clone.LinkedList.Last!.Value, cloneOther).ShouldBeTrue();
        ReferenceEquals(clone.ObservableCollection, clone.ObservableCollectionAlias).ShouldBeTrue();
        ReferenceEquals(clone.ObservableCollection, observableCollection).ShouldBeFalse();
        ReferenceEquals(clone.ObservableCollection[0], clone).ShouldBeTrue();
        ReferenceEquals(clone.ObservableCollection[1], cloneOther).ShouldBeTrue();
        ReferenceEquals(clone.ReadOnlyCollection, clone.ReadOnlyCollectionAlias).ShouldBeTrue();
        ReferenceEquals(clone.ReadOnlyCollection, readOnlyCollection).ShouldBeFalse();
        ReferenceEquals(clone.ReadOnlyCollection[0], clone).ShouldBeTrue();
        ReferenceEquals(clone.ReadOnlyCollection[1], cloneOther).ShouldBeTrue();
        clone.BlockingCollection.BoundedCapacity.ShouldBe(2);

        var parent = new SharedMutableCollectionRoot();
        var child = new SharedMutableCollectionRoot();
        var cyclicImmutableList = ImmutableList.Create(child);
        parent.ImmutableList = child.ImmutableList = cyclicImmutableList;
        var graphClone = parent.DeepClone();
        ReferenceEquals(graphClone.ImmutableList, graphClone.ImmutableList.Single().ImmutableList)
            .ShouldBeTrue();
        ReferenceEquals(graphClone.ImmutableList.Single(), child).ShouldBeFalse();
    }
}
