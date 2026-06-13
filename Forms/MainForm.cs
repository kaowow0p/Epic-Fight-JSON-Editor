using EpicFightJsonGeneratorApp.Models;
using EpicFightJsonGeneratorApp.Services;
using System.Text.Json;

namespace EpicFightJsonGeneratorApp.Forms;

public sealed class MainForm : Form
{
    private const int GuiPreviewBoxSize = 128;
    private const int DialogWidth = 420;

    private readonly ItemModelScanner _scanner = new();
    private readonly EpicFightJsonGenerator _generator = new();

    private readonly TextBox _projectFolderTextBox = new();
    private readonly TextBox _outputFolderTextBox = new();
    private readonly ListBox _itemsListBox = new();
    private readonly TextBox _itemNameTextBox = new();
    private readonly GroupBox _guiPreviewGroupBox = new();
    private readonly Panel _guiPreviewImageBox = new();
    private readonly PictureBox _guiPreviewPictureBox = new();
    private readonly Label _previewStatusLabel = new();
    private readonly TextBox _impactTextBox = new();
    private readonly TextBox _maxStrikesTextBox = new();
    private readonly ComboBox _typeComboBox = new();
    private readonly Button _scanButton = new();
    private readonly Button _createButton = new();
    private readonly Label _statusLabel = new();

    private IReadOnlyList<ItemModelInfo> _allModels = Array.Empty<ItemModelInfo>();
    private bool _isOutputFolderManuallySelected;

    public MainForm()
    {
        Text = "Epic Fight JSON Generator";
        Width = 820;
        Height = 640;
        MinimumSize = new Size(720, 500);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        SetDefaultValues();
    }

    private void BuildLayout()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(CreateFolderPanel(), 0, 0);
        root.Controls.Add(CreateMainContentPanel(), 0, 1);
        root.Controls.Add(_statusLabel, 0, 2);

        _statusLabel.AutoSize = true;
        _statusLabel.Padding = new Padding(0, 8, 0, 0);

        Controls.Add(root);
    }

    private Control CreateFolderPanel()
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Button browseProjectButton = new() { Text = "Browse", AutoSize = true };
        browseProjectButton.Click += BrowseProjectButton_Click;

        Button browseJarButton = new() { Text = "Browse JAR...", AutoSize = true };
        browseJarButton.Click += BrowseJarButton_Click;

        _scanButton.Text = "Scan";
        _scanButton.AutoSize = true;
        _scanButton.Click += ScanButton_Click;

        panel.Controls.Add(new Label { Text = "Project / JAR", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        panel.Controls.Add(_projectFolderTextBox, 1, 0);
        panel.Controls.Add(CreateButtonPanel(browseProjectButton, browseJarButton, _scanButton), 2, 0);

        Button browseOutputButton = new() { Text = "Browse...", AutoSize = true };
        browseOutputButton.Click += BrowseOutputButton_Click;

        panel.Controls.Add(new Label { Text = "Output Folder", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        panel.Controls.Add(_outputFolderTextBox, 1, 1);
        panel.Controls.Add(browseOutputButton, 2, 1);

        _projectFolderTextBox.Dock = DockStyle.Fill;
        _outputFolderTextBox.Dock = DockStyle.Fill;

        return panel;
    }

    private Control CreateMainContentPanel()
    {
        SplitContainer splitContainer = new()
        {
            Dock = DockStyle.Fill
        };

        splitContainer.HandleCreated += (_, _) => ConfigureSplitContainer(splitContainer);

        GroupBox itemsGroupBox = new()
        {
            Text = "Handheld Items",
            Dock = DockStyle.Fill
        };
        _itemsListBox.Dock = DockStyle.Fill;
        _itemsListBox.SelectedIndexChanged += ItemsListBox_SelectedIndexChanged;
        itemsGroupBox.Controls.Add(_itemsListBox);

        splitContainer.Panel1.Controls.Add(itemsGroupBox);
        splitContainer.Panel2.Controls.Add(CreateSelectedItemPanel());

        return splitContainer;
    }

    private static void ConfigureSplitContainer(SplitContainer splitContainer)
    {
        splitContainer.Panel1MinSize = 240;
        splitContainer.Panel2MinSize = 300;

        int availableWidth = splitContainer.Width - splitContainer.SplitterWidth;
        int maxDistance = availableWidth - splitContainer.Panel2MinSize;

        if (maxDistance >= splitContainer.Panel1MinSize)
        {
            splitContainer.SplitterDistance = Math.Min(330, maxDistance);
        }
    }

    private Control CreateSelectedItemPanel()
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 1,
            RowCount = 5
        };

        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        panel.Controls.Add(CreateItemNamePanel(), 0, 0);
        panel.Controls.Add(CreateGuiPreviewPanel(), 0, 1);
        panel.Controls.Add(CreateParametersGroupBox(), 0, 2);
        panel.Controls.Add(CreateActionsPanel(), 0, 3);

        return panel;
    }

    private Control CreateItemNamePanel()
    {
        TableLayoutPanel panel = CreateSingleFieldPanel("Item Name", _itemNameTextBox);
        _itemNameTextBox.ReadOnly = true;
        return panel;
    }

    private Control CreateGuiPreviewPanel()
    {
        _guiPreviewGroupBox.Text = "GUI Preview - not found";
        _guiPreviewGroupBox.Dock = DockStyle.Fill;

        FlowLayoutPanel previewPanel = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        _guiPreviewImageBox.Size = new Size(GuiPreviewBoxSize, GuiPreviewBoxSize);
        _guiPreviewImageBox.BorderStyle = BorderStyle.FixedSingle;
        _guiPreviewImageBox.BackColor = Color.White;
        _guiPreviewImageBox.Margin = Padding.Empty;

        _guiPreviewPictureBox.SizeMode = PictureBoxSizeMode.Normal;
        _guiPreviewPictureBox.BorderStyle = BorderStyle.None;
        _guiPreviewPictureBox.Visible = false;

        _previewStatusLabel.Dock = DockStyle.Fill;
        _previewStatusLabel.Text = "Preview not found";
        _previewStatusLabel.TextAlign = ContentAlignment.MiddleCenter;

        _guiPreviewImageBox.Controls.Add(_guiPreviewPictureBox);
        _guiPreviewImageBox.Controls.Add(_previewStatusLabel);
        previewPanel.Controls.Add(_guiPreviewImageBox);
        _guiPreviewGroupBox.Controls.Add(previewPanel);

        return _guiPreviewGroupBox;
    }

    private Control CreateParametersGroupBox()
    {
        GroupBox groupBox = new()
        {
            Text = "Epic Fight Parameters",
            Dock = DockStyle.Top,
            AutoSize = true
        };

        groupBox.Controls.Add(CreateParametersPanel());
        return groupBox;
    }

    private Control CreateParametersPanel()
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Top,
            Padding = new Padding(8),
            AutoSize = true,
            ColumnCount = 2
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddInputRow(panel, 0, "Impact", _impactTextBox);
        AddInputRow(panel, 1, "Max Strikes", _maxStrikesTextBox);
        AddInputRow(panel, 2, "Type", _typeComboBox);

        _typeComboBox.DropDownStyle = ComboBoxStyle.DropDown;

        return panel;
    }

    private static TableLayoutPanel CreateSingleFieldPanel(string labelText, TextBox textBox)
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 8)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label label = new()
        {
            Text = labelText,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 4)
        };

        textBox.Dock = DockStyle.Top;
        textBox.Margin = Padding.Empty;

        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(textBox, 0, 1);

        return panel;
    }

    private Control CreateActionsPanel()
    {
        FlowLayoutPanel panel = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        };

        _createButton.Text = "Create JSON file";
        _createButton.AutoSize = true;
        _createButton.Click += CreateButton_Click;

        panel.Controls.Add(_createButton);
        return panel;
    }

    private static FlowLayoutPanel CreateButtonPanel(params Control[] controls)
    {
        FlowLayoutPanel panel = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty
        };

        panel.Controls.AddRange(controls);
        return panel;
    }

    private static void AddInputRow(TableLayoutPanel panel, int rowIndex, string labelText, Control input)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label label = new()
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 8)
        };

        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 5, 0, 5);

        panel.Controls.Add(label, 0, rowIndex);
        panel.Controls.Add(input, 1, rowIndex);
    }

    private void SetDefaultValues()
    {
        _impactTextBox.Text = "2";
        _maxStrikesTextBox.Text = "2";
        _typeComboBox.Items.AddRange(new object[]
        {
            "epicfight:axe",
            "epicfight:sword",
            "epicfight:spear",
            "epicfight:greatsword",
            "epicfight:dagger"
        });
        _typeComboBox.Text = "epicfight:axe";
    }

    private void BrowseProjectButton_Click(object? sender, EventArgs e)
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Select Minecraft mod root folder or src/main/resources folder",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _projectFolderTextBox.Text = dialog.SelectedPath;
        _isOutputFolderManuallySelected = false;
        _outputFolderTextBox.Clear();
    }

    private void BrowseJarButton_Click(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Select Minecraft mod JAR file",
            Filter = "Minecraft mod JAR (*.jar)|*.jar|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _projectFolderTextBox.Text = dialog.FileName;
        _isOutputFolderManuallySelected = false;
        _outputFolderTextBox.Clear();
    }

    private void BrowseOutputButton_Click(object? sender, EventArgs e)
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Select output folder for Epic Fight JSON files",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _isOutputFolderManuallySelected = true;
            _outputFolderTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void ScanButton_Click(object? sender, EventArgs e)
    {
        await ScanItemsAsync();
    }

    private async Task ScanItemsAsync()
    {
        if (!ValidationHelper.TryValidateScanInput(_projectFolderTextBox.Text, out string errorMessage))
        {
            ShowError(errorMessage);
            return;
        }

        SetBusyState(true);

        try
        {
            _allModels = await _scanner.ScanHandheldItemsAsync(_projectFolderTextBox.Text);
            BindItems(_allModels);
            _statusLabel.Text = $"Found {_allModels.Count} handheld item(s).";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private void BindItems(IReadOnlyList<ItemModelInfo> items)
    {
        _itemsListBox.SelectedIndexChanged -= ItemsListBox_SelectedIndexChanged;
        _itemsListBox.DataSource = null;
        _itemsListBox.DisplayMember = nameof(ItemModelInfo.ItemName);
        _itemsListBox.DataSource = items;
        _itemsListBox.SelectedIndexChanged += ItemsListBox_SelectedIndexChanged;

        ClearSelectedItemDetails();

        if (items.Count > 0)
        {
            _itemsListBox.SelectedIndex = 0;
            UpdateSelectedItemDetails(items[0]);
        }
    }

    private void ItemsListBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_itemsListBox.SelectedItem is not ItemModelInfo selectedItem)
        {
            return;
        }

        UpdateSelectedItemDetails(selectedItem);
    }

    private void UpdateSelectedItemDetails(ItemModelInfo selectedItem)
    {
        _itemNameTextBox.Text = selectedItem.ItemName;
        _guiPreviewGroupBox.Text = $"GUI Preview - {GetGuiModelDisplayName(selectedItem.GuiModelReference)}";
        SuggestOutputFolder(selectedItem);
        SetPreviewImage(selectedItem.GuiTextureFilePath, selectedItem.GuiTextureBytes, GetPreviewMissingMessage(selectedItem));
    }

    private async void CreateButton_Click(object? sender, EventArgs e)
    {
        await CreateJsonFileAsync();
    }

    private async Task CreateJsonFileAsync()
    {
        ItemModelInfo? selectedItem = _itemsListBox.SelectedItem as ItemModelInfo;
        if (!ValidationHelper.TryValidateGenerationInput(
                _projectFolderTextBox.Text,
                _outputFolderTextBox.Text,
                selectedItem,
                _impactTextBox.Text,
                _maxStrikesTextBox.Text,
                _typeComboBox.Text,
                out GenerationInput input,
                out string errorMessage))
        {
            ShowError(errorMessage);
            return;
        }

        string outputPath = Path.Combine(input.OutputFolder, $"{input.ItemName}.json");
        bool overwrite = ConfirmOverwriteIfNeeded(outputPath);
        if (File.Exists(outputPath) && !overwrite)
        {
            return;
        }

        SetBusyState(true);

        try
        {
            string createdPath = await _generator.SaveWeaponCapabilityAsync(
                input.OutputFolder,
                input.ItemName,
                input.Impact,
                input.MaxStrikes,
                input.Type,
                overwrite);

            ShowSilentMessage("Success", $"JSON file created successfully{Environment.NewLine}{createdPath}");

            _statusLabel.Text = $"Created: {createdPath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private bool ConfirmOverwriteIfNeeded(string outputPath)
    {
        if (!File.Exists(outputPath))
        {
            return true;
        }

        return ShowSilentConfirmation(
            "Confirm overwrite",
            $"File already exists:{Environment.NewLine}{outputPath}{Environment.NewLine}{Environment.NewLine}Overwrite it?");
    }

    private void SuggestOutputFolder(ItemModelInfo selectedItem)
    {
        if (_isOutputFolderManuallySelected)
        {
            return;
        }

        try
        {
            string sourcePath = _projectFolderTextBox.Text;
            string baseFolder = IsJarFile(sourcePath)
                ? Path.GetDirectoryName(sourcePath) ?? string.Empty
                : _scanner.ResolveResourcesFolder(sourcePath);

            _outputFolderTextBox.Text = Path.Combine(
                baseFolder,
                "data",
                selectedItem.ModId,
                "capabilities",
                "weapons");
        }
        catch
        {
            _outputFolderTextBox.Clear();
        }
    }

    private void ClearSelectedItemDetails()
    {
        _itemNameTextBox.Clear();
        _guiPreviewGroupBox.Text = "GUI Preview - not found";
        SetPreviewImage(null, null, "Preview not found");
    }

    private static string GetGuiModelDisplayName(string? guiModelReference)
    {
        if (string.IsNullOrWhiteSpace(guiModelReference))
        {
            return "not found";
        }

        string normalizedReference = guiModelReference.Replace('\\', '/');
        int slashIndex = normalizedReference.LastIndexOf('/');

        return slashIndex >= 0
            ? normalizedReference[(slashIndex + 1)..]
            : normalizedReference[(normalizedReference.LastIndexOf(':') + 1)..];
    }

    private static string GetPreviewMissingMessage(ItemModelInfo selectedItem)
    {
        if (string.Equals(selectedItem.GuiResolveStatus, "GUI model missing", StringComparison.OrdinalIgnoreCase))
        {
            return "GUI model not found";
        }

        if (string.Equals(selectedItem.GuiResolveStatus, "no perspectives.gui.parent", StringComparison.OrdinalIgnoreCase))
        {
            return "No GUI parent in item model";
        }

        if (string.Equals(selectedItem.GuiResolveStatus, "GUI model found, texture missing", StringComparison.OrdinalIgnoreCase))
        {
            return "Texture not found";
        }

        return selectedItem.GuiResolveError ?? "Preview not found";
    }

    private void SetPreviewImage(string? imagePath, byte[]? imageBytes, string missingMessage)
    {
        Image? previousImage = _guiPreviewPictureBox.Image;
        _guiPreviewPictureBox.Image = null;
        previousImage?.Dispose();
        _guiPreviewPictureBox.Visible = false;
        _guiPreviewPictureBox.Size = Size.Empty;

        bool hasImageBytes = imageBytes is { Length: > 0 };
        bool hasImageFile = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);

        if (!hasImageBytes && !hasImageFile)
        {
            _previewStatusLabel.Text = missingMessage;
            _previewStatusLabel.Visible = true;
            return;
        }

        try
        {
            using Stream stream = imageBytes is { Length: > 0 }
                ? new MemoryStream(imageBytes)
                : File.OpenRead(imagePath!);
            using Image image = Image.FromStream(stream);
            Bitmap previewImage = CreateScaledPreviewImage(image);
            _guiPreviewPictureBox.Size = previewImage.Size;
            _guiPreviewPictureBox.Location = GetCenteredPreviewLocation(previewImage.Size);
            _guiPreviewPictureBox.Image = previewImage;
            _guiPreviewPictureBox.Visible = true;
            _previewStatusLabel.Visible = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _previewStatusLabel.Text = "Texture found but image failed to load";
            _previewStatusLabel.Visible = true;
        }
    }

    private static bool IsJarFile(string path)
    {
        return File.Exists(path)
            && string.Equals(Path.GetExtension(path), ".jar", StringComparison.OrdinalIgnoreCase);
    }

    private static Bitmap CreateScaledPreviewImage(Image image)
    {
        float scale = Math.Min(
            (float)GuiPreviewBoxSize / image.Width,
            (float)GuiPreviewBoxSize / image.Height);

        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
        Bitmap scaledImage = new(width, height);

        using Graphics graphics = Graphics.FromImage(scaledImage);
        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.DrawImage(image, new Rectangle(0, 0, width, height));

        return scaledImage;
    }

    private static Point GetCenteredPreviewLocation(Size imageSize)
    {
        return new Point(
            Math.Max(0, (GuiPreviewBoxSize - imageSize.Width) / 2),
            Math.Max(0, (GuiPreviewBoxSize - imageSize.Height) / 2));
    }

    private void SetBusyState(bool isBusy)
    {
        _scanButton.Enabled = !isBusy;
        _createButton.Enabled = !isBusy;
        Cursor = isBusy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void ShowError(string message)
    {
        ShowSilentMessage("Error", message);
        _statusLabel.Text = message;
    }

    private void ShowSilentMessage(string title, string message)
    {
        using Form dialog = CreateSilentDialog(title, message);
        Button okButton = CreateDialogButton("OK", DialogResult.OK);

        FlowLayoutPanel buttonPanel = CreateDialogButtonPanel();
        buttonPanel.Controls.Add(okButton);

        dialog.Controls.Add(buttonPanel);
        dialog.AcceptButton = okButton;
        dialog.ShowDialog(this);
    }

    private bool ShowSilentConfirmation(string title, string message)
    {
        using Form dialog = CreateSilentDialog(title, message);
        Button yesButton = CreateDialogButton("Yes", DialogResult.Yes);
        Button noButton = CreateDialogButton("No", DialogResult.No);

        FlowLayoutPanel buttonPanel = CreateDialogButtonPanel();
        buttonPanel.Controls.Add(noButton);
        buttonPanel.Controls.Add(yesButton);

        dialog.Controls.Add(buttonPanel);
        dialog.AcceptButton = yesButton;
        dialog.CancelButton = noButton;

        return dialog.ShowDialog(this) == DialogResult.Yes;
    }

    private static Form CreateSilentDialog(string title, string message)
    {
        Form dialog = new()
        {
            Text = title,
            Width = DialogWidth,
            Height = 180,
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false
        };

        Label messageLabel = new()
        {
            Text = message,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            AutoEllipsis = true
        };

        dialog.Controls.Add(messageLabel);
        return dialog;
    }

    private static FlowLayoutPanel CreateDialogButtonPanel()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
    }

    private static Button CreateDialogButton(string text, DialogResult dialogResult)
    {
        return new Button
        {
            Text = text,
            DialogResult = dialogResult,
            AutoSize = true,
            MinimumSize = new Size(84, 28)
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _guiPreviewPictureBox.Image?.Dispose();
        }

        base.Dispose(disposing);
    }
}
