using System.ComponentModel;
using Bosun.Panel.Mvvm;
using Xunit;

namespace Bosun.Panel.Tests.Helpers;

/// <summary>The view model base (D02 T02 §2): a property that changes says so, once, by name.</summary>
public class ObservableTests
{
    private sealed class Model : Observable
    {
        private string _name = "";
        private int _count;

        public string Name
        {
            get => _name;
            set => LastSetChanged = Set(ref _name, value);
        }

        public int Count
        {
            get => _count;
            set
            {
                if (Set(ref _count, value)) Raise(nameof(Summary));
            }
        }

        public string Summary => $"{_count} waiting";

        public bool LastSetChanged { get; private set; }
    }

    [Fact]
    public void A_changed_value_is_announced_once_by_name_and_the_same_value_again_is_not()
    {
        var model = new Model();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.Name = "Queue";
        Assert.True(model.LastSetChanged);
        Assert.Equal(["Name"], changed);

        model.Name = "Queue";
        Assert.False(model.LastSetChanged);
        Assert.Equal(["Name"], changed);

        model.Count = 3; // and a property worked out from another is raised by name
        Assert.Equal(["Name", "Count", "Summary"], changed);
        Assert.Equal("3 waiting", model.Summary);
    }
}
