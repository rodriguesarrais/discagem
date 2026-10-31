using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Discagem.Client.ViewModels;

namespace Discagem.Client.Views;

public partial class ChatPanel : UserControl
{
    private ShellViewModel? _viewModel;
    private ChannelViewModel? _channel;
    private StreamViewerViewModel? _stream;
    private bool _stickToBottom = true;
    private bool _loadingOlder;

    public ChatPanel()
    {
        InitializeComponent();
        MessageScroller.ScrollChanged += OnScrollChanged;
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as ShellViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        AttachChannel(_viewModel?.SelectedChannel);
        AttachStream(_viewModel?.ActiveStream);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateStreamLayout(resetHeight: false);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.SelectedChannel):
                AttachChannel(_viewModel?.SelectedChannel);
                Composer.Focus();
                break;
            case nameof(ShellViewModel.ActiveStream):
                AttachStream(_viewModel?.ActiveStream);
                break;
        }
    }

    // ---- Messages and scrolling ---------------------------------------------------------------

    private void AttachChannel(ChannelViewModel? channel)
    {
        if (_channel is not null)
        {
            _channel.Messages.CollectionChanged -= OnMessagesChanged;
        }

        _channel = channel;
        if (_channel is not null)
        {
            _channel.Messages.CollectionChanged += OnMessagesChanged;
        }

        _stickToBottom = true;
        ScrollToEndSoon();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_loadingOlder)
        {
            return;
        }

        var ownMessage = e.Action == NotifyCollectionChangedAction.Add &&
                         e.NewItems?.OfType<MessageItemViewModel>().Any(m => m.IsMine) == true;
        if (_stickToBottom || ownMessage || e.Action == NotifyCollectionChangedAction.Reset)
        {
            ScrollToEndSoon();
        }
    }

    private void ScrollToEndSoon()
    {
        Dispatcher.UIThread.Post(() =>
        {
            MessageScroller.UpdateLayout();
            MessageScroller.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scroller = MessageScroller;
        var maxOffset = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        if (e.ExtentDelta.Y == 0 || _stickToBottom == false)
        {
            _stickToBottom = scroller.Offset.Y >= maxOffset - 48;
        }

        if (scroller.Offset.Y < 200 && e.OffsetDelta.Y < 0)
        {
            _ = LoadOlderAsync();
        }
    }

    private async Task LoadOlderAsync()
    {
        if (_loadingOlder || _viewModel is null)
        {
            return;
        }

        _loadingOlder = true;
        try
        {
            var scroller = MessageScroller;
            var oldExtent = scroller.Extent.Height;
            var oldOffset = scroller.Offset.Y;
            var added = await _viewModel.LoadOlderAsync();
            if (added > 0)
            {
                scroller.UpdateLayout();
                var delta = scroller.Extent.Height - oldExtent;
                scroller.Offset = new Vector(scroller.Offset.X, oldOffset + delta);
            }
        }
        finally
        {
            _loadingOlder = false;
        }
    }

    // ---- Composer -----------------------------------------------------------------------------

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (_viewModel.SendCommand.CanExecute(null))
            {
                _viewModel.SendCommand.Execute(null);
            }
        }
        else if (e.Key == Key.Up && string.IsNullOrEmpty(Composer.Text))
        {
            e.Handled = _viewModel.EditLastOwnMessage();
        }
        else if (e.Key == Key.V && e.KeyModifiers.HasFlag(TopLevel.GetTopLevel(this)?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control))
        {
            // Files and images (screenshots) become attachments; anything else is pasted as text.
            e.Handled = true;
            _ = PasteAsync();
        }
    }

    private async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (_viewModel is null || clipboard is null)
        {
            return;
        }

        try
        {
            var files = (await clipboard.TryGetFilesAsync())?.OfType<IStorageFile>().ToList();
            if (files is { Count: > 0 })
            {
                await _viewModel.AddAttachmentsAsync(files);
                return;
            }

            using var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is not null)
            {
                using var png = new MemoryStream();
                bitmap.Save(png);
                _viewModel.AddPastedImage(png.ToArray());
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Clipboard contents Discagem cannot read: fall back to pasting text.
        }

        Composer.Paste();
    }

    private void OnEditBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        box.GetObservable(IsVisibleProperty).Subscribe(new ActionObserver<bool>(visible =>
        {
            if (visible)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.CaretIndex = box.Text?.Length ?? 0;
                });
            }
            else if (box.IsKeyboardFocusWithin)
            {
                Composer.Focus();
            }
        }));
    }

    // ---- Drag and drop --------------------------------------------------------------------------

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (HasFiles(e))
        {
            DropOverlay.IsVisible = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (_viewModel is null)
        {
            return;
        }

        var files = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().ToList();
        if (files is { Count: > 0 })
        {
            await _viewModel.AddAttachmentsAsync(files);
            Composer.Focus();
        }
    }

    // ---- Stream viewer layout -------------------------------------------------------------------

    private void AttachStream(StreamViewerViewModel? stream)
    {
        if (_stream is not null)
        {
            _stream.PropertyChanged -= OnStreamPropertyChanged;
        }

        _stream = stream;
        if (_stream is not null)
        {
            _stream.PropertyChanged += OnStreamPropertyChanged;
        }

        UpdateStreamLayout(resetHeight: true);
    }

    private void OnStreamPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StreamViewerViewModel.IsPoppedOut))
        {
            UpdateStreamLayout(resetHeight: true);
        }
    }

    private void UpdateStreamLayout(bool resetHeight)
    {
        var rows = LayoutRoot.RowDefinitions;
        var showInline = _stream is { IsPoppedOut: false };
        if (!showInline)
        {
            rows[2].Height = new GridLength(0);
            rows[3].Height = new GridLength(0);
            StreamSplitter.IsVisible = false;
            return;
        }

        var available = Math.Max(0, Bounds.Height - 52);
        var current = rows[2].Height.IsAbsolute ? rows[2].Height.Value : 0;
        if (resetHeight || current < 1)
        {
            current = Math.Round(available * 0.58);
        }

        // Keep room for the composer and a few messages.
        current = Math.Clamp(current, Math.Min(200, available * 0.5), Math.Max(200, available - 220));
        rows[2].Height = new GridLength(current);
        rows[3].Height = new GridLength(5);
        StreamSplitter.IsVisible = true;
    }

    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => onNext(value);
    }
}
