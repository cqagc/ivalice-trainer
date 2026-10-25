using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

public sealed partial class TrainerTests
{
    private static GameSnapshot InventorySnapshot(byte quantity = 1, long generation = 1) =>
        new(generation, 0, [], Enumerable.Repeat(quantity, FftLayout.InventoryCount).ToArray(),
            Poaching: Enumerable.Repeat(quantity, FftLayout.PoachingCount).ToArray());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedInventorySnapshotDoesNotNotifyFieldsCollectionsOrPending(bool dirty)
    {
        var model = new TrainerViewModel(new TestMemory());
        var snapshot = InventorySnapshot();
        model.ApplySnapshot(snapshot);
        if (dirty)
            model.Items[3].Quantity.Value = 42;
        var rows = model.VisibleItems.ToArray();
        var pending = model.Pending.ToArray();
        var fieldEvents = 0;
        var visibleEvents = 0;
        var pendingEvents = 0;
        var pendingProperties = 0;
        foreach (var item in model.Items)
            item.Quantity.PropertyChanged += (_, _) => fieldEvents++;
        model.VisibleItems.CollectionChanged += (_, _) => visibleEvents++;
        model.Pending.CollectionChanged += (_, _) => pendingEvents++;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(model.HasChanges) or nameof(model.HasJpChanges)
                or nameof(model.NeedsConfirmation))
                pendingProperties++;
        };
        for (var i = 0; i < 40; i++)
            model.ApplySnapshot(snapshot);
        Assert.Equal(0, fieldEvents);
        Assert.Equal(0, visibleEvents);
        Assert.Equal(0, pendingEvents);
        Assert.Equal(0, pendingProperties);
        Assert.Equal(rows, model.VisibleItems.ToArray());
        Assert.Equal(pending, model.Pending.ToArray());
    }

    [Fact]
    public void SnapshotReconcilesVisibilityOnlyAfterAllQuantitiesHaveRefreshed()
    {
        var model = new TrainerViewModel(new TestMemory());
        model.ApplySnapshot(InventorySnapshot());
        var next = InventorySnapshot(0);
        next.Inventory[7] = 9;
        next.Poaching![3] = 8;
        var events = 0;
        model.VisibleItems.CollectionChanged += (_, _) =>
        {
            events++;
            // Any early per-quantity refilter would observe other rows' old values.
            Assert.All(model.Items, item => Assert.Equal(
                (uint)(item.IsPoaching ? next.Poaching[item.Id] : next.Inventory[item.Id]), item.Quantity.Value));
        };
        model.ApplySnapshot(next);
        Assert.True(events > 0);
        Assert.Equal(2, model.VisibleItems.Count);
        Assert.Equal(7, model.VisibleItems[0].Id);
        Assert.Equal(3, model.VisibleItems[1].Id);
        Assert.True(model.VisibleItems[1].IsPoaching);
        Assert.Empty(model.Pending);
    }

    [Fact]
    public void FailedSnapshotDoesNotLeaveCollectionRefreshSuspended()
    {
        var model = new TrainerViewModel(new TestMemory());
        model.ApplySnapshot(InventorySnapshot());
        System.ComponentModel.PropertyChangedEventHandler fail = (_, e) =>
        {
            if (e.PropertyName == nameof(EditableField.Value))
                throw new InvalidOperationException("simulated subscriber failure");
        };
        model.Items[1].Quantity.PropertyChanged += fail;
        Assert.Throws<InvalidOperationException>(() => model.ApplySnapshot(InventorySnapshot(0)));
        model.Items[1].Quantity.PropertyChanged -= fail;
        model.ApplySnapshot(InventorySnapshot(0));
        Assert.Empty(model.VisibleItems);
        model.Items[5].Quantity.Value = 7;
        Assert.Same(model.Items[5], Assert.Single(model.VisibleItems));
        Assert.Same(model.Items[5].Quantity, Assert.Single(model.Pending));
    }

    [Fact]
    public void RefreshProtectsEditingAndDirtyValuesButRevertUsesLatestEvenIfValueWasUnchanged()
    {
        var field = new EditableField("quantity", "Inventory", 0, 1, 0, 99, 5);
        var events = 0;
        field.PropertyChanged += (_, _) => events++;
        field.Refresh(5);
        Assert.Equal(0, events);
        field.IsEditing = true;
        field.Refresh(8);
        Assert.Equal(5u, field.Value);
        Assert.Equal(0, events);
        field.IsEditing = false;
        field.Revert();
        Assert.Equal(8u, field.Value);
        field.Value = 42;
        events = 0;
        field.Refresh(11);
        field.Refresh(11);
        Assert.Equal(42u, field.Value);
        Assert.Equal(8u, field.Original);
        Assert.Equal(0, events);
        field.Revert();
        Assert.Equal(11u, field.Value);
        Assert.False(field.IsDirty);
    }

    [Fact]
    public void InventoryDisconnectAndGenerationResetDoNotReuseOldDirtyRows()
    {
        var model = new TrainerViewModel(new TestMemory());
        model.ApplySnapshot(InventorySnapshot());
        var old = model.Items[0];
        old.Quantity.IsEditing = true;
        old.Quantity.Value = 42;
        model.ApplySnapshot(null);
        Assert.False(model.CanEdit);
        Assert.False(model.MaxVisibleItems.CanExecute(null));
        Assert.Same(old, model.Items[0]);
        model.ApplySnapshot(InventorySnapshot(3));
        Assert.Same(old, model.Items[0]);
        Assert.Equal(42u, old.Quantity.Value);
        old.Quantity.IsEditing = false;
        model.Undo.Execute(null);
        Assert.Equal(3u, old.Quantity.Value);
        old.Quantity.Value = 88;
        model.ApplySnapshot(InventorySnapshot(7, 2));
        Assert.NotSame(old, model.Items[0]);
        Assert.Equal(FftLayout.InventoryCount + FftLayout.PoachingCount, model.Items.Count);
        Assert.All(model.Items, item => Assert.Equal(7u, item.Quantity.Value));
        Assert.Empty(model.Pending);
        var current = model.Items.ToArray();
        model.ApplySnapshot(InventorySnapshot(9, 2));
        Assert.Equal(current, model.Items.ToArray());
        Assert.All(model.Items, item => Assert.Equal(9u, item.Quantity.Value));
    }

    [Fact]
    public void MaxAndUndoBatchPendingWhileRespectingSearchCategoryAndShowAll()
    {
        var model = new TrainerViewModel(new TestMemory());
        model.ApplySnapshot(InventorySnapshot(0));
        Assert.Empty(model.VisibleItems);
        model.ShowAllItems = true;
        model.ShowPoaching.Execute(null);
        Assert.Equal(FftLayout.PoachingCount, model.VisibleItems.Count);
        model.ItemSearch = model.VisibleItems[3].HexId;
        Assert.NotEmpty(model.VisibleItems);
        var targets = model.VisibleItems.ToArray();
        var propertyEvents = 0;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.HasChanges))
                propertyEvents++;
        };
        model.MaxVisibleItems.Execute(null);
        Assert.Equal(1, propertyEvents);
        Assert.Equal(targets.Length, model.Pending.Count);
        Assert.All(targets, item => Assert.Equal(99u, item.Quantity.Value));
        Assert.All(model.Items.Except(targets), item => Assert.Equal(0u, item.Quantity.Value));
        model.ShowAllItems = false;
        Assert.Equal(targets, model.VisibleItems.ToArray());
        model.Undo.Execute(null);
        Assert.Equal(2, propertyEvents);
        Assert.Empty(model.Pending);
        Assert.Empty(model.VisibleItems);
        model.ItemSearch = "";
        model.ShowAllItems = true;
        model.ShowBonusItems.Execute(null);
        Assert.Equal(4, model.VisibleItems.Count);
        model.ShowConsumables.Execute(null);
        Assert.All(model.VisibleItems, item => Assert.Equal("consumable", item.GroupKey));
        model.ShowEquipment.Execute(null);
        Assert.All(model.VisibleItems, item => Assert.Equal("equipment", item.GroupKey));
    }
}
