using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Windowsyikefu.Services;
using Windowsyikefu.ViewModels;

namespace Windowsyikefu.Views;

/// <summary>
/// 转发时挑目标会话。这里只负责「选谁」，真正的发送仍由主窗口调用接口，
/// 避免发送逻辑在两个地方各写一份。
/// </summary>
public partial class ForwardWindow : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 与其它窗口统一：能开系统毛玻璃就用透明底，开不成退回不透明浅灰
        WindowEffects.ApplyBackdrop(this);
    }

    private readonly ICollectionView _view;
    private string _keyword = "";

    /// <summary>用户选中的目标会话；点了取消就是 0</summary>
    public long TargetSessionId { get; private set; }

    public string TargetTitle { get; private set; } = "";

    public ForwardWindow(IEnumerable<SessionItem> sessions, string preview)
    {
        InitializeComponent();

        var list = new ObservableCollection<SessionItem>(sessions);
        _view = CollectionViewSource.GetDefaultView(list);
        _view.Filter = Match;
        ListSessions.ItemsSource = _view;

        TxtPreview.Text = preview;

        // 默认选中第一条，回车即可转发
        ListSessions.SelectedIndex = 0;
        UpdateHint();
    }

    private bool Match(object item) => item is SessionItem s &&
        (string.IsNullOrWhiteSpace(_keyword) ||
         s.Title.Contains(_keyword, StringComparison.OrdinalIgnoreCase) ||
         s.Id.ToString().Contains(_keyword, StringComparison.OrdinalIgnoreCase));

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _keyword = TxtSearch.Text.Trim();
        _view.Refresh();

        // 过滤后原来的选中项可能已经不在列表里，落回第一条
        if (ListSessions.SelectedItem is not SessionItem selected || !_view.Contains(selected))
            ListSessions.SelectedIndex = 0;

        UpdateHint();
    }

    private void ListSessions_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHint();

    private void UpdateHint()
    {
        var has = ListSessions.SelectedItem is SessionItem;
        BtnForward.IsEnabled = has;

        TxtHint.Text = has
            ? "双击会话可直接转发"
            : "没有可转发的会话";
    }

    private void ListSessions_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListSessions.SelectedItem is SessionItem) Confirm();
    }

    private void BtnForward_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (ListSessions.SelectedItem is not SessionItem target) return;

        TargetSessionId = target.Id;
        TargetTitle = target.Title;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
