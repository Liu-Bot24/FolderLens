using System.Collections;
using System.Collections.Specialized;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class SnapshotRefreshTests
{
    [Fact] public async Task IdentityRefinementPreservesInteriorFilesButRejectsChangedVersionsAndReorders()
    {
        await using var catalog=await Fixture();var filter=new FilterSpec{RootId="benchmark"};
        await Change(catalog,"UPDATE Files SET entry_state='missing' WHERE entry_id IN('000000000003','000000000008')");
        var before=await catalog.CreateSnapshot(filter,1,1);
        await Change(catalog,"UPDATE Files SET entry_state='present' WHERE entry_id IN('000000000003','000000000008'); UPDATE Files SET file_version=2 WHERE entry_id='000000000005'");
        var after=await catalog.CreateSnapshot(filter,1,2);var coarse=await catalog.CompareSnapshots(before,after,[],[],CancellationToken.None);
        var detailed=(await catalog.RefineSnapshotChanges(before,after,[],[],coarse,CancellationToken.None))[""];
        Assert.NotNull(detailed);Assert.Equal(1,detailed.Sum(e=>e.Removed));Assert.Equal(3,detailed.Sum(e=>e.Added));
        var old=await catalog.ReadPage(before.Id,0);var next=await catalog.ReadPage(after.Id,0);
        var mirror=old.Select(i=>(i.EntryId,i.Version)).ToList();int delta=0;
        foreach(var edit in detailed){int index=edit.Prefix+delta;mirror.RemoveRange(index,edit.Removed);mirror.InsertRange(index,next.Skip(index).Take(edit.Added).Select(i=>(i.EntryId,i.Version)));delta+=edit.Added-edit.Removed;}
        Assert.Equal(next.Select(i=>(i.EntryId,i.Version)),mirror);
        var reverse=await catalog.CreateSnapshot(filter with{Sort=new("name","desc")},1,3);
        var reversedChanges=await catalog.CompareSnapshots(after,reverse,[],[],CancellationToken.None);
        Assert.Null((await catalog.RefineSnapshotChanges(after,reverse,[],[],reversedChanges,CancellationToken.None))[""]);
    }
    [Fact] public void SeparatedEditsPreserveAnchorsAndEveryIntermediateCollectionContract()
    {
        var random=new Random(7341);
        for(int sample=0;sample<200;sample++)
        {
            var old=Enumerable.Range(0,80).Select(i=>new object()).ToList();var next=old.ToList();
            for(int i=0;i<15;i++){int position=random.Next(next.Count+1);if(position<next.Count&&random.Next(2)==0)next.RemoveAt(position);else next.Insert(position,new object());}
            if(sample%3==0)next.Reverse();
            var splice=new SnapshotSplice(0,old.Count,next.Count);var ranges=splice.Preserve(old.Select((row,i)=>(Old:i,New:next.IndexOf(row))).Where(pair=>pair.New>=0));
            var view=new VirtualRangeCollection<object>(old.Count,i=>old[i],value=>old.IndexOf(value!));var mirror=old.ToList();
            view.CollectionChanged+=(_,args)=>
            {
                if(args.Action==NotifyCollectionChangedAction.Remove){Assert.Equal(mirror.Skip(args.OldStartingIndex).Take(args.OldItems!.Count),args.OldItems.Cast<object>());mirror.RemoveRange(args.OldStartingIndex,args.OldItems.Count);}
                else if(args.Action==NotifyCollectionChangedAction.Add)mirror.InsertRange(args.NewStartingIndex,args.NewItems!.Cast<object>());
                else throw new InvalidOperationException("Unexpected reset");
                Assert.Equal(mirror,view.Cast<object>());
                for(int i=0;i<mirror.Count;i++)Assert.Equal(mirror.IndexOf(mirror[i]),view.IndexOf(mirror[i]));
                Assert.Equal(-1,view.IndexOf(new object()));
            };
            view.UpdateRanges(ranges,i=>next[i],value=>next.IndexOf(value!));Assert.Equal(next,view.Cast<object>());
        }
    }
    [Fact] public async Task RepeatedIdenticalSnapshotsDoNotResetOrReplaceDisplayedObjects()
    {
        await using var catalog=await Fixture();var filter=new FilterSpec{RootId="benchmark"};
        var previous=await catalog.CreateSnapshot(filter,1,1);var rows=await catalog.ReadPage(previous.Id,0);
        var view=new VirtualRangeCollection<SnapshotItem>(rows.Count,index=>rows[index]);int notifications=0;
        view.CollectionChanged+=(_,_)=>notifications++;object first=view[0]!;
        for(int iteration=0;iteration<3;iteration++)
        {
            var next=await catalog.CreateSnapshot(filter,1,iteration+2);var changes=await catalog.CompareSnapshots(previous,next,[],[],CancellationToken.None);var edit=changes[""];
            Assert.Equal(new SnapshotSplice(10,0,0),edit);view.Update(edit.Prefix,edit.Removed,edit.Added,index=>rows[index]);previous=next;
        }
        Assert.Equal(0,notifications);Assert.Same(first,view[0]);
    }
    [Fact] public async Task InsertDeleteAndVersionChangesProduceExactOrderWithoutReset()
    {
        await using var catalog=await Fixture();var filter=new FilterSpec{RootId="benchmark"};
        await Change(catalog,"UPDATE Files SET entry_state='missing' WHERE entry_id IN('000000000003','000000000008')");
        var before=await catalog.CreateSnapshot(filter,1,1);var old=await catalog.ReadPage(before.Id,0);
        await Change(catalog,"UPDATE Files SET entry_state='present' WHERE entry_id='000000000003'; UPDATE Files SET entry_state='missing' WHERE entry_id='000000000006'; UPDATE Files SET file_version=2 WHERE entry_id='000000000004'");
        var after=await catalog.CreateSnapshot(filter,1,2);var next=await catalog.ReadPage(after.Id,0);var edit=(await catalog.CompareSnapshots(before,after,[],[],CancellationToken.None))[""];
        Assert.Equal(2,edit.Prefix);Assert.Null(edit.MapOldIndex(2));Assert.Equal(7,edit.MapOldIndex(7));
        var view=new VirtualRangeCollection<SnapshotItem>(old.Count,index=>old[index]);var mirror=old.ToList();int notifications=0;
        view.CollectionChanged+=(_,change)=>
        {
            Assert.NotEqual(NotifyCollectionChangedAction.Reset,change.Action);notifications++;
            if(change.Action==NotifyCollectionChangedAction.Remove)mirror.RemoveRange(change.OldStartingIndex,change.OldItems!.Count);
            else if(change.Action==NotifyCollectionChangedAction.Add)mirror.InsertRange(change.NewStartingIndex,change.NewItems!.Cast<SnapshotItem>());
            Assert.Equal(mirror.Select(row=>row.EntryId),view.Cast<SnapshotItem>().Select(row=>row.EntryId));
        };
        view.Update(edit.Prefix,edit.Removed,edit.Added,index=>next[index]);
        Assert.Equal(edit.Removed+edit.Added,notifications);Assert.Equal(next,view.Cast<SnapshotItem>().ToArray());
        Assert.Equal(2,view.Cast<SnapshotItem>().Single(row=>row.EntryId=="000000000004").Version);
    }
    [Fact] public async Task NewGroupBeforeExistingGroupDoesNotInvalidateExistingGroupItems()
    {
        await using var catalog=await Fixture();
        await Change(catalog,"""
            INSERT INTO Directories(directory_id,root_id,parent_id,name,relative_path,canonical_key,case_mode) VALUES('nested','benchmark','benchmark-dir','nested','nested','nested','sensitive');
            UPDATE Files SET directory_id='nested',relative_path='nested\\'||name WHERE logical_bytes>2048;
            UPDATE Files SET entry_state='missing' WHERE logical_bytes<=2048;
            """);
        var filter=new FilterSpec{RootId="benchmark",Grouping=new(true)};var before=await catalog.CreateSnapshot(filter,1,1);var old=await catalog.ReadGroups(before.Id);
        await Change(catalog,"UPDATE Files SET entry_state='present'");
        var after=await catalog.CreateSnapshot(filter,1,2);var next=await catalog.ReadGroups(after.Id);var edits=await catalog.CompareSnapshots(before,after,old,next,CancellationToken.None);
        Assert.Equal(1,old.Count);Assert.Equal(2,next.Count);Assert.Equal(0,old[0].Start);Assert.Equal(2,next.Single(group=>group.Id==old[0].Id).Start);
        Assert.Equal(new SnapshotSplice(8,0,0),edits[old[0].Id]);
        var matching=await catalog.ReadSnapshotEntries(after.Id,["000000000003"],CancellationToken.None);
        Assert.Equal(2,Assert.Single(matching).Ordinal);Assert.Equal(old[0].Id,matching[0].Group!.Id);
    }
    [Fact] public void AppendAndRemoveAllHaveConsistentCountDuringNotifications()
    {
        var view=new VirtualRangeCollection<int>(2,index=>index);var mirror=new List<int>{0,1};
        view.CollectionChanged+=(_,e)=>
        {
            Assert.NotEqual(NotifyCollectionChangedAction.Reset,e.Action);
            if(e.Action==NotifyCollectionChangedAction.Add)mirror.InsertRange(e.NewStartingIndex,e.NewItems!.Cast<int>());
            if(e.Action==NotifyCollectionChangedAction.Remove)mirror.RemoveRange(e.OldStartingIndex,e.OldItems!.Count);
            Assert.Equal(mirror,view.Cast<int>());
        };
        view.Update(2,0,3,index=>index);Assert.Equal(5,view.Count);view.Update(0,5,0,_=>throw new Exception());Assert.Empty((IEnumerable)view);
    }
    private static async Task<CatalogStore> Fixture(){var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-refresh",Guid.NewGuid().ToString("N")));await catalog.Initialize();await catalog.SeedBenchmark(10);return catalog;}
    private static Task<int> Change(CatalogStore catalog,string sql)=>catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText=sql;return command.ExecuteNonQuery();});
}
