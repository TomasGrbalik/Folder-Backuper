global using Xunit;

// The localization types appear in almost every test now that services return message codes, so they
// are reached globally rather than repeated as a using in each file.
global using FolderBackuper.Infrastructure.Localization;
