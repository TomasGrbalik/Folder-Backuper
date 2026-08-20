using System.Diagnostics.CodeAnalysis;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

using FolderBackuper.Infrastructure.Localization;
namespace FolderBackuper.Features.Notifications;

/// <summary>
/// Reads and writes the single global notification configuration.
/// </summary>
/// <remarks>
/// These writes deliberately do not pass through <see cref="ConfigurationMutationGate.ExecuteAsync"/>.
/// That gate refuses configuration changes while any run is pending, but a rejected API key must be
/// fixable exactly when backups are running and failing to notify. The row is a single record edited
/// by one local user, so last-write-wins is sufficient.
/// </remarks>
public sealed class NotificationSettingsService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    ISecretProtector secretProtector,
    InstallationIdentityService installationIdentity,
    TimeProvider timeProvider)
{
    private const int MaxRecipients = 50;

    private static readonly char[] RecipientSeparators = ['\n', '\r', ',', ';'];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<NotificationSettingsView> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ReadAsync(cancellationToken);
        if (settings is null)
        {
            return new NotificationSettingsView(false, null, null, [], false, null);
        }

        var provider = ReadProviderConfiguration(settings.NotificationProviderConfiguration);
        return new NotificationSettingsView(
            provider.Enabled,
            provider.FromAddress,
            provider.FromName,
            ReadRecipients(settings.RecipientList),
            settings.ProtectedNotificationSecret is { Length: > 0 },
            settings.UpdatedAtUtc);
    }

    /// <summary>
    /// Returns the configuration a provider needs, or null when nothing deliverable is saved. The
    /// unprotected key exists only inside the returned object for the duration of one attempt.
    /// </summary>
    public async Task<NotificationDeliveryConfiguration?> GetDeliveryConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await ReadAsync(cancellationToken);
        if (settings?.ProtectedNotificationSecret is not { Length: > 0 } protectedKey) return null;

        var provider = ReadProviderConfiguration(settings.NotificationProviderConfiguration);
        var recipients = ReadRecipients(settings.RecipientList);
        if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.FromAddress) || recipients.Count == 0)
        {
            return null;
        }

        string apiKey;
        try
        {
            apiKey = secretProtector.Unprotect(protectedKey);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A key protected under a different machine or profile cannot be recovered. Treat it as
            // absent rather than propagating, so a delivery attempt fails safely instead of throwing.
            return null;
        }

        return string.IsNullOrEmpty(apiKey)
            ? null
            : new NotificationDeliveryConfiguration(apiKey, provider.FromAddress, provider.FromName, recipients);
    }

    public async Task<NotificationSettingsResult> SaveAsync(
        SaveNotificationSettingsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The settings row is created on demand elsewhere too; reuse that so a first save converges
        // on the same singleton primary key instead of inserting a competing row.
        await installationIdentity.GetInstallationIdAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await context.ApplicationSettings.SingleAsync(cancellationToken);
        var storedKey = settings.ProtectedNotificationSecret;
        var replacementKey = command.ApiKey?.Trim();
        var keepStoredKey = string.IsNullOrEmpty(replacementKey);

        if (!TryNormalize(command, keepStoredKey, storedKey is { Length: > 0 }, out var normalized, out var errors))
        {
            return NotificationSettingsResult.Invalid(NotificationResultMessage.SettingsInvalid, errors);
        }

        settings.NotificationProvider = NotificationProviders.Resend;
        settings.NotificationProviderConfiguration = JsonSerializer.Serialize(
            new ProviderConfiguration(normalized.Enabled, normalized.FromAddress, normalized.FromName),
            SerializerOptions);
        settings.RecipientList = JsonSerializer.Serialize(normalized.Recipients, SerializerOptions);
        settings.ProtectedNotificationSecret = keepStoredKey
            ? storedKey
            : secretProtector.Protect(replacementKey!);
        settings.UpdatedAtUtc = timeProvider.GetUtcNow();

        await context.SaveChangesAsync(cancellationToken);
        return NotificationSettingsResult.Success(normalized.Enabled
            ? NotificationResultMessage.SettingsSaved
            : NotificationResultMessage.SettingsSavedNotificationsOff);
    }

    /// <summary>Splits a newline-, comma-, or semicolon-separated recipient list into addresses.</summary>
    public static IReadOnlyList<string> ParseRecipients(string? value) => value is null
        ? []
        : value.Split(RecipientSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryNormalize(
        SaveNotificationSettingsCommand command,
        bool keepStoredKey,
        bool hasStoredKey,
        [NotNullWhen(true)] out NormalizedSettings? normalized,
        out IReadOnlyDictionary<string, UiMessage> errors)
    {
        var found = new Dictionary<string, UiMessage>(StringComparer.Ordinal);
        normalized = null;

        var fromAddress = command.FromAddress?.Trim() ?? "";
        var fromName = command.FromName?.Trim();
        var recipients = ParseRecipients(command.Recipients)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Only a configuration that is turned on has to be complete. Turning notifications off must
        // stay possible even while the saved settings are incomplete or wrong.
        if (command.Enabled)
        {
            if (fromAddress.Length == 0)
            {
                found[nameof(command.FromAddress)] = UiMessage.For(NotificationResultMessage.SenderAddressRequired);
            }
            else if (!IsEmailAddress(fromAddress))
            {
                found[nameof(command.FromAddress)] = UiMessage.For(NotificationResultMessage.SenderAddressInvalid);
            }

            if (recipients.Count == 0)
            {
                found[nameof(command.Recipients)] = UiMessage.For(NotificationResultMessage.RecipientRequired);
            }

            if (keepStoredKey && !hasStoredKey)
            {
                found[nameof(command.ApiKey)] = UiMessage.For(NotificationResultMessage.ApiKeyRequired);
            }
        }

        if (recipients.Count > MaxRecipients)
        {
            found[nameof(command.Recipients)] = UiMessage.For(NotificationResultMessage.TooManyRecipients, UiMessageArgument.FromNumber(MaxRecipients));
        }
        else
        {
            var invalid = recipients.Where(address => !IsEmailAddress(address)).ToList();
            if (invalid.Count > 0)
            {
                found[nameof(command.Recipients)] = UiMessage.For(NotificationResultMessage.RecipientAddressInvalid, UiMessageArgument.FromText(string.Join(", ", invalid)));
            }
        }

        if (fromName is { Length: > 200 })
        {
            found[nameof(command.FromName)] = UiMessage.For(NotificationResultMessage.SenderNameTooLong);
        }

        errors = found;
        if (found.Count > 0) return false;

        normalized = new NormalizedSettings(
            command.Enabled,
            fromAddress.Length == 0 ? null : fromAddress,
            string.IsNullOrEmpty(fromName) ? null : fromName,
            recipients);
        return true;
    }

    private static bool IsEmailAddress(string value)
    {
        // MailAddress accepts display-name forms such as "Name <a@b>"; a plain address is required
        // so that what is saved is exactly what a provider receives.
        if (!MailAddress.TryCreate(value, out var address)) return false;
        return string.Equals(address.Address, value, StringComparison.Ordinal)
            && address.Host.Contains('.', StringComparison.Ordinal);
    }

    private async Task<ApplicationSettings?> ReadAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ApplicationSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
    }

    private static IReadOnlyList<string> ReadRecipients(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            // Unreadable stored settings must not break the settings page; an empty list surfaces
            // as "not configured" and the user can save a correct value over it.
            return [];
        }
    }

    private static ProviderConfiguration ReadProviderConfiguration(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ProviderConfiguration(false, null, null);
        try
        {
            return JsonSerializer.Deserialize<ProviderConfiguration>(json, SerializerOptions)
                ?? new ProviderConfiguration(false, null, null);
        }
        catch (JsonException)
        {
            return new ProviderConfiguration(false, null, null);
        }
    }

    private sealed record ProviderConfiguration(bool Enabled, string? FromAddress, string? FromName);

    private sealed record NormalizedSettings(
        bool Enabled, string? FromAddress, string? FromName, IReadOnlyList<string> Recipients);
}
