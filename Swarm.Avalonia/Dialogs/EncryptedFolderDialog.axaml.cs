using Avalonia.Controls;
using Avalonia.Interactivity;
using Swarm.Core.Models;
using Swarm.Core.Services;

namespace Swarm.Avalonia.Dialogs;

public partial class EncryptedFolderDialog : Window
{
    private readonly FolderEncryptionService? _encryptionService;
    private readonly EncryptedFolder? _existingFolder;
    private readonly bool _isUnlockMode;

    /// <summary>
    /// Result of the dialog operation.
    /// </summary>
    public bool Success { get; private set; }

    /// <summary>
    /// Constructor for creating a new encrypted folder.
    /// </summary>
    public EncryptedFolderDialog(FolderEncryptionService encryptionService)
    {
        InitializeComponent();
        _encryptionService = encryptionService;
        _isUnlockMode = false;
        
        ConfigureForCreateMode();
    }

    /// <summary>
    /// Constructor for unlocking an existing encrypted folder.
    /// </summary>
    public EncryptedFolderDialog(FolderEncryptionService encryptionService, EncryptedFolder folder)
    {
        InitializeComponent();
        _encryptionService = encryptionService;
        _existingFolder = folder;
        _isUnlockMode = true;
        
        ConfigureForUnlockMode();
    }

    private void ConfigureForCreateMode()
    {
        TitleText.Text = "Create Encrypted Folder";
        SubtitleText.Text = "Files will be encrypted at rest with AES-256";
        ActionButton.Content = "Create Folder";
        FolderNamePanel.IsVisible = true;
        ConfirmPasswordPanel.IsVisible = true;
        WarningPanel.IsVisible = true;
    }

    private void ConfigureForUnlockMode()
    {
        TitleText.Text = "Unlock Encrypted Folder";
        SubtitleText.Text = _existingFolder?.FolderPath ?? "Enter password to access files";
        ActionButton.Content = "Unlock";
        FolderNamePanel.IsVisible = false;
        ConfirmPasswordPanel.IsVisible = false;
        WarningPanel.IsVisible = false;
    }

    private void ActionButton_Click(object? sender, RoutedEventArgs e)
    {
        HideError();

        if (_isUnlockMode)
        {
            PerformUnlock();
        }
        else
        {
            PerformCreate();
        }
    }

    private void PerformCreate()
    {
        var folderName = FolderNameInput.Text?.Trim();
        var password = PasswordInput.Text;
        var confirmPassword = ConfirmPasswordInput.Text;

        // Validation
        if (string.IsNullOrEmpty(folderName))
        {
            ShowError("Please enter a folder name.");
            return;
        }

        if (folderName.Contains('/') || folderName.Contains('\\') || folderName.Contains(':'))
        {
            ShowError("Folder name cannot contain path separators.");
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            ShowError("Please enter a password.");
            return;
        }

        if (password.Length < 8)
        {
            ShowError("Password must be at least 8 characters.");
            return;
        }

        if (password != confirmPassword)
        {
            ShowError("Passwords do not match.");
            return;
        }

        // Create folder
        if (_encryptionService != null)
        {
            var success = _encryptionService.CreateEncryptedFolder(folderName, password);
            if (success)
            {
                Success = true;
                Close();
            }
            else
            {
                ShowError("Failed to create encrypted folder. Check if folder already exists.");
            }
        }
    }

    private void PerformUnlock()
    {
        var password = PasswordInput.Text;

        if (string.IsNullOrEmpty(password))
        {
            ShowError("Please enter the password.");
            return;
        }

        if (_encryptionService != null && _existingFolder != null)
        {
            var success = _encryptionService.UnlockFolder(_existingFolder.FolderPath, password);
            if (success)
            {
                Success = true;
                Close();
            }
            else
            {
                ShowError("Incorrect password.");
            }
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.IsVisible = true;
    }

    private void HideError()
    {
        ErrorPanel.IsVisible = false;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Success = false;
        Close();
    }
}
