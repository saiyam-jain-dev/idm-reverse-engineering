using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace DownloadManager;

public sealed class Form1 : Form
{
    private static readonly Color AccentColor = Color.FromArgb(30, 105, 180);
    private static readonly Color AccentHoverColor = Color.FromArgb(24, 87, 151);
    private static readonly Color SurfaceColor = Color.White;
    private static readonly Color WindowColor = Color.FromArgb(242, 246, 251);
    private static readonly Color TextColor = Color.FromArgb(37, 49, 64);
    private static readonly Color MutedTextColor = Color.FromArgb(103, 116, 132);
    private static readonly Color BorderColor = Color.FromArgb(220, 228, 237);
    private readonly BindingList<DownloadJob> _jobs = [];
    private readonly BindingList<DownloadJob> _visibleJobs = [];
    private readonly TextBox _urlTextBox = new();
    private readonly TextBox _folderTextBox = new();
    private readonly DataGridView _downloadsGrid = new();
    private readonly TreeView _categoriesTree = new();
    private readonly Button _startButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _resumeButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _stopAllButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _clearCompletedButton = new();
    private readonly Button _openFileButton = new();
    private readonly Label _statusLabel = new();
    private readonly Label _summaryLabel = new();
    private readonly HashSet<string> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _pendingBrowserHandoffs = [];
    private readonly DesktopBridgeServer _bridgeServer;
    private string _selectedView = "all";
    private Task? _queueTask;
    private DownloadJob? _activeJob;
    private CancellationTokenSource? _activeCancellation;
    private bool _isClosing;
    private bool _allowClose;
    private bool _queueSaveErrorShown;

    public Form1()
    {
        Text = "Download Manager";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 600);
        Size = new Size(1180, 740);
        Font = new Font("Segoe UI", 9F);
        BackColor = WindowColor;

        var downloadsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        _folderTextBox.Text = downloadsFolder;
        BuildLayout();
        FormClosing += HandleFormClosing;
        LoadQueue();
        RefreshVisibleJobs();
        UpdateActionButtons();
        _bridgeServer = new DesktopBridgeServer(HandleBridgeRequestAsync, ReportBridgeError);
        _bridgeServer.Start();
        _statusLabel.Text = "Desktop bridge ready for the Chrome extension.";
        if (_jobs.Any(job => job.State == DownloadState.Queued))
        {
            StartQueue(this, EventArgs.Empty);
        }
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Controls.Add(layout);

        var menu = BuildMenu();
        layout.Controls.Add(menu, 0, 0);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = false,
            Padding = new Padding(2, 8, 0, 0),
            BackColor = SurfaceColor,
            Margin = Padding.Empty
        };
        ConfigureButton(_startButton, "Start queue", StartQueue, primary: true);
        ConfigureButton(_pauseButton, "Pause", PauseSelected);
        ConfigureButton(_resumeButton, "Resume", ResumeSelected);
        ConfigureButton(_cancelButton, "Cancel", CancelSelected, destructive: true);
        ConfigureButton(_stopAllButton, "Stop all", StopAll);
        ConfigureButton(_deleteButton, "Delete selected", DeleteSelected, destructive: true);
        ConfigureButton(_clearCompletedButton, "Clear completed", ClearCompleted);
        ConfigureButton(_openFileButton, "Open file", OpenSelectedFile);
        toolbar.Controls.AddRange(
        [
            _startButton, _pauseButton, _resumeButton, _cancelButton, _stopAllButton,
            _deleteButton, _clearCompletedButton, _openFileButton
        ]);
        layout.Controls.Add(toolbar, 0, 1);

        var inputs = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            BackColor = SurfaceColor,
            Padding = new Padding(8, 6, 8, 6),
            Margin = Padding.Empty
        };
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        inputs.Controls.Add(new Label
        {
            Text = "URL",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = MutedTextColor
        }, 0, 0);
        _urlTextBox.Dock = DockStyle.Fill;
        _urlTextBox.BorderStyle = BorderStyle.FixedSingle;
        _urlTextBox.Font = new Font("Segoe UI", 9.5F);
        _urlTextBox.PlaceholderText = "Paste a direct HTTP or HTTPS file link";
        _urlTextBox.Margin = new Padding(0, 3, 10, 3);
        _urlTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                AddDownload(this, EventArgs.Empty);
                e.SuppressKeyPress = true;
            }
        };
        inputs.Controls.Add(_urlTextBox, 1, 0);
        _folderTextBox.Dock = DockStyle.Fill;
        _folderTextBox.BorderStyle = BorderStyle.FixedSingle;
        _folderTextBox.Font = new Font("Segoe UI", 9F);
        _folderTextBox.Margin = new Padding(0, 3, 8, 3);
        inputs.Controls.Add(_folderTextBox, 2, 0);

        var browseButton = new Button
        {
            Text = "Choose folder",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(247, 250, 253),
            ForeColor = TextColor,
            Margin = new Padding(0, 2, 8, 2)
        };
        browseButton.FlatAppearance.BorderColor = BorderColor;
        browseButton.Click += BrowseForFolder;
        inputs.Controls.Add(browseButton, 3, 0);

        var addButton = new Button
        {
            Text = "Add download",
            Dock = DockStyle.Fill,
            BackColor = AccentColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 2)
        };
        addButton.FlatAppearance.BorderSize = 0;
        addButton.FlatAppearance.MouseOverBackColor = AccentHoverColor;
        addButton.Click += AddDownload;
        inputs.Controls.Add(addButton, 4, 0);
        layout.Controls.Add(inputs, 0, 2);

        var content = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = new Size(1000, 600),
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 205,
            BackColor = BorderColor,
            Margin = new Padding(0, 10, 0, 0),
            SplitterWidth = 5
        };
        content.Panel1.BackColor = Color.FromArgb(248, 250, 253);
        content.Panel2.BackColor = SurfaceColor;
        BuildCategories();
        content.Panel1.Controls.Add(_categoriesTree);
        BuildDownloadsGrid();
        content.Panel2.Controls.Add(_downloadsGrid);
        layout.Controls.Add(content, 0, 3);

        var statusBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = SurfaceColor,
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(0, 8, 0, 0)
        };
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = MutedTextColor;
        _statusLabel.Text = "Add a direct HTTP or HTTPS download to get started.";
        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleRight;
        _summaryLabel.ForeColor = TextColor;
        _summaryLabel.Font = new Font(Font, FontStyle.Bold);
        statusBar.Controls.Add(_statusLabel, 0, 0);
        statusBar.Controls.Add(_summaryLabel, 1, 0);
        layout.Controls.Add(statusBar, 0, 4);
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip
        {
            Dock = DockStyle.Fill,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = SurfaceColor,
            ForeColor = TextColor,
            Padding = new Padding(8, 4, 0, 4)
        };
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("Add download...", null, AddDownload);
        file.DropDownItems.Add("Open download folder", null, (_, _) => OpenFolder(_folderTextBox.Text));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Exit", null, (_, _) => Close());

        var downloads = new ToolStripMenuItem("&Downloads");
        downloads.DropDownItems.Add("Start queue", null, StartQueue);
        downloads.DropDownItems.Add("Pause selected", null, PauseSelected);
        downloads.DropDownItems.Add("Resume selected", null, ResumeSelected);
        downloads.DropDownItems.Add("Cancel selected", null, CancelSelected);
        downloads.DropDownItems.Add("Stop all", null, StopAll);
        downloads.DropDownItems.Add(new ToolStripSeparator());
        downloads.DropDownItems.Add("Delete selected", null, DeleteSelected);
        downloads.DropDownItems.Add("Clear completed", null, ClearCompleted);

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add("All downloads", null, (_, _) => SelectView("all"));
        view.DropDownItems.Add("Active downloads", null, (_, _) => SelectView("active"));
        view.DropDownItems.Add("Queued downloads", null, (_, _) => SelectView("queued"));
        view.DropDownItems.Add("Finished downloads", null, (_, _) => SelectView("finished"));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("About Download Manager", null, (_, _) =>
            MessageBox.Show(
                this,
                "An independent download manager for direct HTTP and HTTPS links.",
                "About",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information));
        menu.Items.AddRange([file, downloads, view, help]);
        return menu;
    }

    private void BuildCategories()
    {
        _categoriesTree.Dock = DockStyle.Fill;
        _categoriesTree.BorderStyle = BorderStyle.None;
        _categoriesTree.HideSelection = false;
        _categoriesTree.ShowLines = false;
        _categoriesTree.ShowRootLines = false;
        _categoriesTree.ShowPlusMinus = false;
        _categoriesTree.FullRowSelect = true;
        _categoriesTree.ItemHeight = 28;
        _categoriesTree.Indent = 18;
        _categoriesTree.BackColor = Color.FromArgb(248, 250, 253);
        _categoriesTree.ForeColor = TextColor;
        _categoriesTree.Font = new Font("Segoe UI", 9F);
        _categoriesTree.DrawMode = TreeViewDrawMode.OwnerDrawText;
        _categoriesTree.DrawNode += (_, e) =>
        {
            if (e.Node is null)
            {
                return;
            }

            var selected = e.Node == _categoriesTree.SelectedNode;
            var rowBounds = new Rectangle(0, e.Bounds.Y, _categoriesTree.ClientSize.Width, _categoriesTree.ItemHeight);
            using var background = new SolidBrush(selected
                ? Color.FromArgb(224, 238, 252)
                : _categoriesTree.BackColor);
            e.Graphics.FillRectangle(background, rowBounds);
            var foreground = selected ? AccentColor : TextColor;
            var nodeFont = e.Node.NodeFont ?? _categoriesTree.Font;
            if (e.Node.Tag is "category-root")
            {
                foreground = MutedTextColor;
            }
            TextRenderer.DrawText(
                e.Graphics,
                e.Node.Text,
                nodeFont,
                new Rectangle(e.Bounds.X, rowBounds.Y, Math.Max(0, rowBounds.Right - e.Bounds.X - 8), rowBounds.Height),
                foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        _categoriesTree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is string filter)
            {
                _selectedView = filter;
                RefreshVisibleJobs();
            }
        };

        var root = _categoriesTree.Nodes.Add("All downloads", "All downloads");
        root.Tag = "all";
        AddViewNode("Active", "active");
        AddViewNode("Queue", "queued");
        AddViewNode("Finished", "finished");
        AddViewNode("Unfinished", "unfinished");

        var categories = _categoriesTree.Nodes.Add("Categories", "Categories");
        categories.Tag = "category-root";
        categories.NodeFont = new Font(_categoriesTree.Font, FontStyle.Bold);
        AddViewNode("Compressed", "category:Compressed", categories.Nodes);
        AddViewNode("Documents", "category:Documents", categories.Nodes);
        AddViewNode("Music", "category:Music", categories.Nodes);
        AddViewNode("Programs", "category:Programs", categories.Nodes);
        AddViewNode("Video", "category:Video", categories.Nodes);
        AddViewNode("Other", "category:Other", categories.Nodes);
        _categoriesTree.SelectedNode = root;
        root.Expand();
        categories.Expand();
    }

    private void AddViewNode(string title, string filter, TreeNodeCollection? parent = null)
    {
        var nodes = parent ?? _categoriesTree.Nodes;
        var node = nodes.Add(title, title);
        node.Tag = filter;
    }

    private void BuildDownloadsGrid()
    {
        _downloadsGrid.Dock = DockStyle.Fill;
        _downloadsGrid.AutoGenerateColumns = false;
        _downloadsGrid.AllowUserToAddRows = false;
        _downloadsGrid.AllowUserToDeleteRows = false;
        _downloadsGrid.AllowUserToResizeRows = false;
        _downloadsGrid.ReadOnly = true;
        _downloadsGrid.MultiSelect = false;
        _downloadsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _downloadsGrid.RowHeadersVisible = false;
        _downloadsGrid.BackgroundColor = SurfaceColor;
        _downloadsGrid.BorderStyle = BorderStyle.None;
        _downloadsGrid.GridColor = Color.FromArgb(234, 239, 245);
        _downloadsGrid.EnableHeadersVisualStyles = false;
        _downloadsGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 248, 252);
        _downloadsGrid.ColumnHeadersDefaultCellStyle.ForeColor = TextColor;
        _downloadsGrid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
        _downloadsGrid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 4, 0);
        _downloadsGrid.ColumnHeadersHeight = 40;
        _downloadsGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _downloadsGrid.DefaultCellStyle.BackColor = SurfaceColor;
        _downloadsGrid.DefaultCellStyle.ForeColor = TextColor;
        _downloadsGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 239, 253);
        _downloadsGrid.DefaultCellStyle.SelectionForeColor = TextColor;
        _downloadsGrid.DefaultCellStyle.Padding = new Padding(8, 0, 4, 0);
        _downloadsGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 252, 254);
        _downloadsGrid.RowTemplate.Height = 36;
        _downloadsGrid.RowTemplate.Resizable = DataGridViewTriState.False;
        _downloadsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        AddColumn(nameof(DownloadJob.FileName), "File name", 19, 130);
        AddColumn(nameof(DownloadJob.Source), "Source", 13, 95);
        AddColumn(nameof(DownloadJob.Category), "Category", 9, 80);
        AddColumn(nameof(DownloadJob.SizeText), "Size", 8, 65);
        AddColumn(nameof(DownloadJob.StatusText), "Status", 9, 75);
        AddColumn(nameof(DownloadJob.ProgressText), "Progress", 12, 95);
        AddColumn(nameof(DownloadJob.RateText), "Transfer rate", 10, 90);
        AddColumn(nameof(DownloadJob.TimeLeftText), "Time left", 8, 70);
        AddColumn(nameof(DownloadJob.Details), "Details", 12, 130);
        _downloadsGrid.DataSource = _visibleJobs;
        _downloadsGrid.SelectionChanged += (_, _) => UpdateActionButtons();
        _downloadsGrid.CellDoubleClick += (_, _) => OpenSelectedFile(this, EventArgs.Empty);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Open file", null, OpenSelectedFile);
        contextMenu.Items.Add("Open containing folder", null, OpenSelectedFolder);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Resume / retry", null, ResumeSelected);
        contextMenu.Items.Add("Pause", null, PauseSelected);
        contextMenu.Items.Add("Cancel", null, CancelSelected);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Delete from list", null, DeleteSelected);
        _downloadsGrid.ContextMenuStrip = contextMenu;
        _downloadsGrid.RowPrePaint += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _downloadsGrid.Rows.Count)
            {
                return;
            }

            var row = _downloadsGrid.Rows[e.RowIndex];
            if (row.DataBoundItem is DownloadJob job && job.State == DownloadState.Failed)
            {
                row.DefaultCellStyle.ForeColor = Color.FromArgb(170, 55, 55);
            }
            else
            {
                row.DefaultCellStyle.ForeColor = _downloadsGrid.DefaultCellStyle.ForeColor;
            }
        };
    }

    private void AddColumn(string property, string title, float fillWeight, int minimumWidth)
    {
        _downloadsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = property,
            HeaderText = title,
            FillWeight = fillWeight,
            MinimumWidth = minimumWidth,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private static void ConfigureButton(
        Button button,
        string text,
        EventHandler onClick,
        bool primary = false,
        bool destructive = false)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Height = 38;
        button.Padding = new Padding(9, 0, 9, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Segoe UI", 9F, primary ? FontStyle.Bold : FontStyle.Regular);
        button.FlatAppearance.BorderColor = BorderColor;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 245, 251);
        button.BackColor = primary ? AccentColor : SurfaceColor;
        button.ForeColor = primary ? Color.White : destructive ? Color.FromArgb(170, 61, 61) : TextColor;
        button.Margin = new Padding(0, 0, 9, 0);
        if (primary)
        {
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = AccentHoverColor;
        }
        button.Click += onClick;
    }

    private void BrowseForFolder(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where downloaded files will be saved.",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folderTextBox.Text)
                ? _folderTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folderTextBox.Text = dialog.SelectedPath;
        }
    }

    private void AddDownload(object? sender, EventArgs e)
    {
        var url = _urlTextBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(
                this,
                "Enter a valid HTTP or HTTPS URL.",
                "Invalid URL",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _urlTextBox.Focus();
            return;
        }

        try
        {
            var folder = Path.GetFullPath(_folderTextBox.Text.Trim());
            Directory.CreateDirectory(folder);
            var destinationPath = GetDestinationPath(uri, folder);
            var job = new DownloadJob(url, destinationPath);
            _jobs.Add(job);
            ObserveJob(job);
            RefreshVisibleJobs();
            SaveQueue();
            _downloadsGrid.ClearSelection();
            var rowIndex = _visibleJobs.IndexOf(job);
            if (rowIndex >= 0)
            {
                _downloadsGrid.CurrentCell = _downloadsGrid.Rows[rowIndex].Cells[0];
            }
            _urlTextBox.Clear();
            _statusLabel.Text = $"Starting {job.FileName}.";
            UpdateActionButtons();
            StartQueue(this, EventArgs.Empty);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Could not add download",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private Task<object?> HandleBridgeRequestAsync(JsonElement request)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated)
        {
            throw new InvalidOperationException("The download manager is not accepting extension requests.");
        }

        BeginInvoke((MethodInvoker)(() =>
        {
            try
            {
                completion.SetResult(HandleBridgeRequest(request));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }));
        return completion.Task;
    }

    private object? HandleBridgeRequest(JsonElement request)
    {
        if (_isClosing)
        {
            throw new InvalidOperationException("The desktop application is closing.");
        }

        if (!request.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("The bridge request must include a type.");
        }

        switch (typeElement.GetString())
        {
            case "desktop.status":
                return new { ready = !_isClosing, downloadCount = _jobs.Count };
            case "downloads.list":
                return _jobs.Select(job => new
                {
                    id = job.Id,
                    fileName = job.FileName,
                    source = job.Source,
                    status = job.StatusText,
                    progress = job.ProgressText,
                    size = job.SizeText,
                    rate = job.RateText,
                    timeLeft = job.TimeLeftText
                }).ToArray();
            case "download.add":
            {
                var url = GetRequiredString(request, "url");
                var job = CreateAndAddDownload(url);
                StartQueue(this, EventArgs.Empty);
                _statusLabel.Text = $"Added {job.FileName} from the Chrome extension.";
                return new { id = job.Id, fileName = job.FileName, status = job.StatusText };
            }
            case "download.intercept":
            {
                var url = GetRequiredString(request, "url");
                var suggestedFileName = GetRequiredString(request, "fileName");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                    !string.IsNullOrEmpty(uri.UserInfo))
                {
                    throw new ArgumentException("Only credential-free HTTP and HTTPS downloads can be handled.");
                }

                var source = uri.IdnHost;
                using var dialog = new DownloadInterceptDialog(
                    Path.GetFileName(suggestedFileName),
                    source);
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return new { decision = "browser" };
                }

                var job = CreateAndAddDownload(uri.AbsoluteUri, suggestedFileName, deferUntilHandoff: true);
                _statusLabel.Text = $"Accepted {job.FileName} from Chrome; waiting for the browser handoff.";
                return new { decision = "desktop", id = job.Id, fileName = job.FileName };
            }
            case "download.handoffComplete":
            {
                var job = GetJob(request);
                if (_pendingBrowserHandoffs.Remove(job.Id))
                {
                    if (job.State != DownloadState.Queued)
                    {
                        throw new InvalidOperationException("The browser handoff is no longer queued.");
                    }

                    StartQueue(this, EventArgs.Empty);
                    return new { success = true };
                }
                if (job.State is not (DownloadState.Queued or DownloadState.Downloading or DownloadState.Completed))
                {
                    throw new InvalidOperationException("The browser handoff is no longer pending.");
                }
                return new { success = true };
            }
            case "download.removeQueued":
                RemoveQueuedDownload(GetJob(request));
                return new { success = true };
            case "download.pause":
            {
                var job = GetJob(request);
                if (job.State != DownloadState.Downloading)
                {
                    throw new InvalidOperationException("Only an active download can be paused.");
                }
                PauseJob(job);
                return new { success = true };
            }
            case "download.resume":
            {
                var job = GetJob(request);
                if (job.State is not (DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled))
                {
                    throw new InvalidOperationException("This download cannot be resumed in its current state.");
                }
                ResumeJob(job);
                return new { success = true };
            }
            case "download.cancel":
            {
                var job = GetJob(request);
                if (job.State is not (
                    DownloadState.Queued or DownloadState.Downloading or DownloadState.Pausing or DownloadState.Paused))
                {
                    throw new InvalidOperationException("This download cannot be cancelled in its current state.");
                }
                CancelJob(job);
                return new { success = true };
            }
            case "download.open":
                OpenFile(GetJob(request));
                return new { success = true };
            case "download.show":
            {
                var job = GetJob(request);
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    ArgumentList = { $"/select,{job.DestinationPath}" },
                    UseShellExecute = true
                });
                return new { success = true };
            }
            case "downloads.clearCompleted":
                var completedCount = _jobs.Count(job => job.State == DownloadState.Completed);
                ClearCompleted(this, EventArgs.Empty);
                return new { removed = completedCount };
            case "queue.stop":
                StopAll(this, EventArgs.Empty);
                return new { success = true };
            default:
                throw new InvalidDataException("The requested desktop bridge operation is not supported.");
        }
    }

    private static string GetRequiredString(JsonElement request, string propertyName)
    {
        if (!request.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"The bridge request must include {propertyName}.");
        }

        return value.GetString()!;
    }

    private DownloadJob GetJob(JsonElement request)
    {
        var idText = GetRequiredString(request, "id");
        if (!Guid.TryParse(idText, out var id))
        {
            throw new InvalidDataException("The download ID is invalid.");
        }

        return _jobs.FirstOrDefault(job => job.Id == id)
            ?? throw new InvalidDataException("The requested download no longer exists.");
    }

    private DownloadJob CreateAndAddDownload(
        string url,
        string? suggestedFileName = null,
        bool deferUntilHandoff = false)
    {
        if (url.Length > 8192)
        {
            throw new InvalidDataException("The download URL exceeds the supported length.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Only valid HTTP or HTTPS URLs without embedded credentials are supported.");
        }

        var folder = Path.GetFullPath(_folderTextBox.Text.Trim());
        Directory.CreateDirectory(folder);
        var job = new DownloadJob(uri.AbsoluteUri, GetDestinationPath(uri, folder, suggestedFileName));
        _jobs.Add(job);
        if (deferUntilHandoff)
        {
            _pendingBrowserHandoffs.Add(job.Id);
        }
        ObserveJob(job);
        RefreshVisibleJobs();
        if (!SaveQueue())
        {
            _jobs.Remove(job);
            _pendingBrowserHandoffs.Remove(job.Id);
            _reservedPaths.Remove(job.DestinationPath);
            RefreshVisibleJobs();
            throw new IOException("The new download could not be saved to the local queue.");
        }
        UpdateActionButtons();
        return job;
    }

    private void ReportBridgeError(string message)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke((MethodInvoker)(() => _statusLabel.Text = message));
    }

    private string GetDestinationPath(Uri uri, string folder, string? suggestedFileName = null)
    {
        var encodedName = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var requestedName = !string.IsNullOrWhiteSpace(suggestedFileName)
            ? Path.GetFileName(suggestedFileName)
            : string.IsNullOrWhiteSpace(encodedName)
                ? "download"
                : Uri.UnescapeDataString(encodedName);
        requestedName = Path.GetFileName(requestedName);

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            requestedName = requestedName.Replace(invalidCharacter, '_');
        }

        requestedName = requestedName.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            requestedName = "download";
        }

        var stem = Path.GetFileNameWithoutExtension(requestedName);
        var extension = Path.GetExtension(requestedName);
        var candidate = Path.Combine(folder, requestedName);
        var suffix = 1;

        while (File.Exists(candidate) ||
               File.Exists(candidate + ".part") ||
               _reservedPaths.Contains(candidate))
        {
            candidate = Path.Combine(folder, $"{stem} ({suffix++}){extension}");
        }

        _reservedPaths.Add(candidate);
        return candidate;
    }

    private void StartQueue(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _startButton))
        {
            _pendingBrowserHandoffs.Clear();
        }

        if (_queueTask is { IsCompleted: false })
        {
            return;
        }

        _queueTask = ProcessQueueAsync();
        UpdateActionButtons();
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                if (_isClosing)
                {
                    break;
                }

                var job = _jobs.FirstOrDefault(item =>
                    item.State == DownloadState.Queued &&
                    !_pendingBrowserHandoffs.Contains(item.Id));
                if (job is null)
                {
                    break;
                }

                job.RequestedAction = DownloadAction.None;
                job.SetDetails(string.Empty);
                job.SetState(DownloadState.Downloading);
                _activeJob = job;

                using var cancellation = new CancellationTokenSource();
                _activeCancellation = cancellation;
                UpdateActionButtons();

                try
                {
                    var progress = new Progress<DownloadTransferProgress>(
                        transfer => job.SetProgress(transfer.BytesReceived, transfer.TotalBytes));
                    await DownloadEngine.DownloadAsync(job, progress, cancellation.Token);
                    job.SetDetails(job.DestinationPath);
                    job.SetState(DownloadState.Completed);
                    _statusLabel.Text = $"Finished {job.FileName}.";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    if (job.RequestedAction == DownloadAction.Pause)
                    {
                        job.SetDetails("Partial file kept. Resume depends on server range support.");
                        job.SetState(DownloadState.Paused);
                        _statusLabel.Text = $"Paused {job.FileName}.";
                    }
                    else
                    {
                        if (DeletePartialFile(job))
                        {
                            job.SetState(DownloadState.Cancelled);
                            _statusLabel.Text = $"Cancelled {job.FileName}.";
                        }
                        else
                        {
                            job.SetState(DownloadState.Paused);
                        }
                    }
                }
                catch (Exception exception)
                {
                    job.SetDetails(exception.Message);
                    job.SetState(DownloadState.Failed);
                    _statusLabel.Text = $"Failed {job.FileName}: {exception.Message}";
                }
                finally
                {
                    _activeJob = null;
                    _activeCancellation = null;
                    UpdateActionButtons();
                }
            }
        }
        finally
        {
            _queueTask = null;
            UpdateActionButtons();
            UpdateSummary();
        }
    }

    private void PauseSelected(object? sender, EventArgs e)
    {
        var job = SelectedJob();
        if (job is not null)
        {
            PauseJob(job);
        }
    }

    private void PauseJob(DownloadJob job)
    {
        if (job != _activeJob || job.State != DownloadState.Downloading)
        {
            return;
        }

        job.RequestedAction = DownloadAction.Pause;
        job.SetState(DownloadState.Pausing);
        _activeCancellation?.Cancel();
        UpdateActionButtons();
    }

    private void ResumeSelected(object? sender, EventArgs e)
    {
        var job = SelectedJob();
        if (job is not null)
        {
            ResumeJob(job);
        }
    }

    private void ResumeJob(DownloadJob job)
    {
        if (job.State is not (DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled))
        {
            return;
        }
        job.RequestedAction = DownloadAction.None;
        job.SetDetails(string.Empty);
        job.SetState(DownloadState.Queued);
        StartQueue(this, EventArgs.Empty);
    }

    private void CancelSelected(object? sender, EventArgs e)
    {
        var job = SelectedJob();
        if (job is not null)
        {
            CancelJob(job);
        }
    }

    private void CancelJob(DownloadJob job)
    {
        if (job.State is not (
                DownloadState.Queued or DownloadState.Downloading or DownloadState.Pausing or DownloadState.Paused))
        {
            return;
        }

        job.RequestedAction = DownloadAction.Cancel;
        if (job == _activeJob)
        {
            job.SetState(DownloadState.Cancelling);
            _activeCancellation?.Cancel();
        }
        else
        {
            if (!DeletePartialFile(job))
            {
                job.SetState(DownloadState.Paused);
                SaveQueue();
                UpdateActionButtons();
                return;
            }
            job.SetState(DownloadState.Cancelled);
            _statusLabel.Text = $"Cancelled {job.FileName}.";
        }

        UpdateActionButtons();
    }

    private void StopAll(object? sender, EventArgs e)
    {
        var partialFileCleanupFailed = false;
        foreach (var job in _jobs.ToArray())
        {
            if (job == _activeJob)
            {
                job.RequestedAction = DownloadAction.Cancel;
                job.SetState(DownloadState.Cancelling);
                _activeCancellation?.Cancel();
            }
            else if (job.State is DownloadState.Queued or DownloadState.Paused)
            {
                if (DeletePartialFile(job))
                {
                    job.SetState(DownloadState.Cancelled);
                }
                else
                {
                    partialFileCleanupFailed = true;
                    job.SetState(DownloadState.Paused);
                }
            }
        }

        _statusLabel.Text = partialFileCleanupFailed
            ? "Stopped the queue; downloads with partial-file cleanup errors were kept paused."
            : "Stopped active and queued downloads.";
        SaveQueue();
        UpdateActionButtons();
    }

    private void DeleteSelected(object? sender, EventArgs e)
    {
        var job = SelectedJob();
        if (job is null || job == _activeJob)
        {
            return;
        }

        if (job.State != DownloadState.Completed)
        {
            if (!DeletePartialFile(job))
            {
                return;
            }
        }

        _jobs.Remove(job);
        SaveQueue();
        _statusLabel.Text = $"Removed {job.FileName} from the list.";
        RefreshVisibleJobs();
        UpdateActionButtons();
    }

    private void RemoveQueuedDownload(DownloadJob job)
    {
        if (job.State != DownloadState.Queued || job == _activeJob)
        {
            throw new InvalidOperationException("Only a queued download can be removed during browser handoff.");
        }

        var index = _jobs.IndexOf(job);
        _jobs.Remove(job);
        _pendingBrowserHandoffs.Remove(job.Id);
        _reservedPaths.Remove(job.DestinationPath);
        RefreshVisibleJobs();
        if (!SaveQueue())
        {
            _jobs.Insert(Math.Clamp(index, 0, _jobs.Count), job);
            _reservedPaths.Add(job.DestinationPath);
            RefreshVisibleJobs();
            throw new IOException("The queued download could not be removed from the local queue.");
        }

        UpdateActionButtons();
    }

    private void ClearCompleted(object? sender, EventArgs e)
    {
        var completed = _jobs.Where(job => job.State == DownloadState.Completed).ToArray();
        foreach (var job in completed)
        {
            _jobs.Remove(job);
        }

        if (completed.Length > 0)
        {
            SaveQueue();
        }
        _statusLabel.Text = completed.Length == 0
            ? "There are no finished downloads to clear."
            : $"Removed {completed.Length} finished item(s) from the list. Files were kept.";
        RefreshVisibleJobs();
        UpdateActionButtons();
    }

    private void OpenSelectedFile(object? sender, EventArgs e)
    {
        var job = SelectedJob();
        if (job is null || job.State != DownloadState.Completed || !File.Exists(job.DestinationPath))
        {
            _statusLabel.Text = "Select a completed download to open its file.";
            return;
        }

        try
        {
            OpenFile(job);
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenFile(DownloadJob job)
    {
        if (job.State != DownloadState.Completed || !File.Exists(job.DestinationPath))
        {
            throw new InvalidOperationException("Only a completed download can be opened.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(job.DestinationPath) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException("Could not open the downloaded file.", exception);
        }
    }

    private void OpenSelectedFolder(object? sender, EventArgs e)
    {
        var job = SelectedJob();
        if (job is null)
        {
            return;
        }

        OpenFolder(Path.GetDirectoryName(job.DestinationPath) ?? _folderTextBox.Text);
    }

    private void OpenFolder(string path)
    {
        try
        {
            var folder = Path.GetFullPath(path);
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
            NotSupportedException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Could not open folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SelectView(string view)
    {
        _selectedView = view;
        var node = FindViewNode(_categoriesTree.Nodes, view);
        if (node is not null)
        {
            _categoriesTree.SelectedNode = node;
        }
        else
        {
            RefreshVisibleJobs();
        }
    }

    private static TreeNode? FindViewNode(TreeNodeCollection nodes, string view)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag as string == view)
            {
                return node;
            }

            var child = FindViewNode(node.Nodes, view);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private void RefreshVisibleJobs()
    {
        var selectedJob = SelectedJob();
        var matchingJobs = _jobs.Where(MatchesSelectedView).ToArray();
        _visibleJobs.RaiseListChangedEvents = false;
        _visibleJobs.Clear();
        foreach (var job in matchingJobs)
        {
            _visibleJobs.Add(job);
        }
        _visibleJobs.RaiseListChangedEvents = true;
        _visibleJobs.ResetBindings();
        if (selectedJob is not null)
        {
            var selectedIndex = _visibleJobs.IndexOf(selectedJob);
            if (selectedIndex >= 0 && _downloadsGrid.Rows.Count > selectedIndex)
            {
                _downloadsGrid.CurrentCell = _downloadsGrid.Rows[selectedIndex].Cells[0];
            }
        }
        UpdateSummary();
    }

    private bool MatchesSelectedView(DownloadJob job) => _selectedView switch
    {
        "all" => true,
        "active" => job.State is DownloadState.Downloading or DownloadState.Pausing or DownloadState.Cancelling,
        "queued" => job.State == DownloadState.Queued,
        "finished" => job.State == DownloadState.Completed,
        "unfinished" => job.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Pausing or
            DownloadState.Paused or DownloadState.Failed,
        var category when category.StartsWith("category:", StringComparison.Ordinal) =>
            job.Category.Equals(category["category:".Length..], StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private void UpdateSummary()
    {
        var active = _jobs.Count(job => job.State == DownloadState.Downloading);
        var queued = _jobs.Count(job => job.State == DownloadState.Queued);
        var completed = _jobs.Count(job => job.State == DownloadState.Completed);
        _summaryLabel.Text = $"{_jobs.Count} total   |   {active} active   |   {queued} queued   |   {completed} finished";
        UpdateCategoryCounts(_categoriesTree.Nodes);
    }

    private void UpdateCategoryCounts(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is string filter && filter.StartsWith("category:", StringComparison.Ordinal))
            {
                var category = filter["category:".Length..];
                var count = _jobs.Count(job =>
                    job.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
                node.Text = $"{category} ({count})";
            }
            else if (node.Tag is string view && view != "category-root")
            {
                var count = _jobs.Count(job => view switch
                {
                    "all" => true,
                    "active" => job.State is DownloadState.Downloading or DownloadState.Pausing or DownloadState.Cancelling,
                    "queued" => job.State == DownloadState.Queued,
                    "finished" => job.State == DownloadState.Completed,
                    "unfinished" => job.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Pausing or
                        DownloadState.Paused or DownloadState.Failed,
                    _ => false
                });
                var title = node.Name;
                if (string.IsNullOrEmpty(title))
                {
                    title = node.Text;
                    var suffix = title.LastIndexOf(" (", StringComparison.Ordinal);
                    if (suffix > 0)
                    {
                        title = title[..suffix];
                    }
                    node.Name = title;
                }
                node.Text = $"{title} ({count})";
            }

            UpdateCategoryCounts(node.Nodes);
        }
    }

    private void LoadQueue()
    {
        try
        {
            var savedQueue = DownloadQueueStore.Load();
            if (savedQueue is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(savedQueue.DefaultFolder))
            {
                _folderTextBox.Text = savedQueue.DefaultFolder;
            }

            if (savedQueue.Downloads is null)
            {
                throw new InvalidDataException("The saved queue does not contain a download list.");
            }

            var restoredIds = new HashSet<Guid>();
            var migratedIds = false;
            foreach (var saved in savedQueue.Downloads)
            {
                if (string.IsNullOrWhiteSpace(saved.Url) || string.IsNullOrWhiteSpace(saved.DestinationPath))
                {
                    throw new InvalidDataException("A saved download is missing its URL or destination path.");
                }

                var destinationPath = Path.GetFullPath(saved.DestinationPath);
                var state = saved.State;
                var details = saved.Details ?? string.Empty;
                var bytesReceived = saved.BytesReceived;
                var totalBytes = saved.TotalBytes;
                var partExists = File.Exists(destinationPath + ".part");

                if (!Uri.TryCreate(saved.Url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    state = DownloadState.Failed;
                    details = "The saved URL is invalid. Remove this entry and add it again.";
                }
                else if (state == DownloadState.Completed)
                {
                    if (File.Exists(destinationPath))
                    {
                        bytesReceived = new FileInfo(destinationPath).Length;
                        totalBytes = bytesReceived;
                    }
                    else
                    {
                        state = DownloadState.Failed;
                        details = "The previously downloaded file is no longer present.";
                    }
                }
                else if (state is DownloadState.Downloading or DownloadState.Pausing or DownloadState.Cancelling)
                {
                    state = partExists ? DownloadState.Paused : DownloadState.Queued;
                    details = partExists
                        ? "Restored after restart. Resume the partial download when ready."
                        : "Restored after restart.";
                }
                else if (state == DownloadState.Paused && !partExists)
                {
                    state = DownloadState.Queued;
                }

                if (partExists && state is not (DownloadState.Cancelled or DownloadState.Completed))
                {
                    bytesReceived = new FileInfo(destinationPath + ".part").Length;
                }

                var id = saved.Id;
                if (id == Guid.Empty || !restoredIds.Add(id))
                {
                    id = Guid.NewGuid();
                    restoredIds.Add(id);
                    migratedIds = true;
                }
                var job = new DownloadJob(saved.Url, destinationPath, id);
                job.Restore(state, details, bytesReceived, totalBytes);
                _jobs.Add(job);
                _reservedPaths.Add(destinationPath);
                ObserveJob(job);
            }

            if (migratedIds)
            {
                SaveQueue();
            }

            _statusLabel.Text = $"Restored {savedQueue.Downloads.Count} download(s) from the previous session.";
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException)
        {
            _statusLabel.Text = $"Could not restore the saved queue: {exception.Message}";
            MessageBox.Show(
                this,
                $"The saved download queue could not be loaded. You can still use the application.\n\n{exception.Message}",
                "Queue restore failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private bool SaveQueue()
    {
        try
        {
            DownloadQueueStore.Save(_folderTextBox.Text, _jobs);
            _queueSaveErrorShown = false;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException)
        {
            _statusLabel.Text = $"Could not save the queue: {exception.Message}";
            if (!_queueSaveErrorShown)
            {
                _queueSaveErrorShown = true;
                MessageBox.Show(
                    this,
                    _statusLabel.Text,
                    "Queue could not be saved",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            return false;
        }
    }

    private void ObserveJob(DownloadJob job)
    {
        job.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DownloadJob.State))
            {
                RefreshVisibleJobs();
                UpdateActionButtons();
                SaveQueue();
            }
        };
    }

    private async void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        if (_activeJob is not null)
        {
            _activeJob.RequestedAction = DownloadAction.Pause;
            _activeJob.SetState(DownloadState.Pausing);
            _activeCancellation?.Cancel();
        }

        if (_queueTask is not null)
        {
            await _queueTask;
        }

        await _bridgeServer.DisposeAsync();
        SaveQueue();

        _allowClose = true;
        Close();
    }

    private bool DeletePartialFile(DownloadJob job)
    {
        try
        {
            DownloadEngine.DeletePartialFile(job);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            job.SetDetails($"Could not remove the partial file: {exception.Message}");
            _statusLabel.Text = $"Could not remove the partial file for {job.FileName}: {exception.Message}";
            return false;
        }
    }

    private DownloadJob? SelectedJob() =>
        _downloadsGrid.CurrentRow?.DataBoundItem as DownloadJob;

    private void UpdateActionButtons()
    {
        var selected = SelectedJob();
        var queueRunning = _queueTask is { IsCompleted: false };
        _startButton.Enabled = _jobs.Any(job => job.State == DownloadState.Queued) && !queueRunning;
        _pauseButton.Enabled = selected is not null &&
                               selected == _activeJob &&
                               selected.State == DownloadState.Downloading;
        _resumeButton.Enabled = selected?.State is DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled;
        _cancelButton.Enabled = selected?.State is DownloadState.Queued or DownloadState.Downloading or
            DownloadState.Pausing or DownloadState.Paused;
        _stopAllButton.Enabled = _jobs.Any(job => job.State is DownloadState.Queued or DownloadState.Downloading or
            DownloadState.Pausing or DownloadState.Paused);
        _deleteButton.Enabled = selected is not null && selected != _activeJob;
        _clearCompletedButton.Enabled = _jobs.Any(job => job.State == DownloadState.Completed);
        _openFileButton.Enabled = selected?.State == DownloadState.Completed &&
                                  File.Exists(selected.DestinationPath);
    }
}
