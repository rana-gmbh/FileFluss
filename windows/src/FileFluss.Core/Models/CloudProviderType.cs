// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;

namespace FileFluss.Core.Models;

/// <summary>
/// Every service FileFluss can connect to. The JSON names are the macOS raw values
/// (<c>CloudProviderType.rawValue</c>), so an exported account list reads the same on both
/// platforms.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CloudProviderType>))]
public enum CloudProviderType
{
    [JsonStringEnumMemberName("pCloud")] PCloud,
    [JsonStringEnumMemberName("kDrive")] KDrive,
    [JsonStringEnumMemberName("oneDrive")] OneDrive,
    [JsonStringEnumMemberName("googleDrive")] GoogleDrive,
    [JsonStringEnumMemberName("nextCloud")] NextCloud,
    [JsonStringEnumMemberName("iCloud")] ICloud,
    [JsonStringEnumMemberName("koofr")] Koofr,
    [JsonStringEnumMemberName("dropbox")] Dropbox,
    [JsonStringEnumMemberName("mega")] Mega,
    [JsonStringEnumMemberName("webDAV")] WebDav,
    [JsonStringEnumMemberName("sftp")] Sftp,
    [JsonStringEnumMemberName("ftp")] Ftp,
    [JsonStringEnumMemberName("wordpress")] WordPress,
    [JsonStringEnumMemberName("gmxCloud")] GmxCloud,
    [JsonStringEnumMemberName("s3")] S3,
    [JsonStringEnumMemberName("synologyDrive")] SynologyDrive,
    [JsonStringEnumMemberName("synologyC2")] SynologyC2,
    [JsonStringEnumMemberName("s3Compatible")] S3Compatible,
    [JsonStringEnumMemberName("box")] Box,
    [JsonStringEnumMemberName("seafile")] Seafile,
    [JsonStringEnumMemberName("filen")] Filen,
    [JsonStringEnumMemberName("internxt")] Internxt,
    [JsonStringEnumMemberName("terabox")] TeraBox,
    [JsonStringEnumMemberName("jottacloud")] Jottacloud,
    [JsonStringEnumMemberName("googleDrivePicker")] GoogleDrivePicker,
    [JsonStringEnumMemberName("gopro")] GoPro,
    [JsonStringEnumMemberName("qnap")] Qnap,
    [JsonStringEnumMemberName("ugreen")] Ugreen,
    [JsonStringEnumMemberName("asustor")] Asustor,
}

/// <summary>How the Add Cloud Account picker groups a provider, as on macOS.</summary>
public enum ProviderCategory
{
    CloudStorage,
    OtherProtocols,
    Devices,

    /// <summary>Network storage boxes on the user's own network, signed in to with a NAS account.</summary>
    Nas,
}

public static class CloudProviderTypeExtensions
{
    /// <summary>Brand names — never translated, exactly as on macOS.</summary>
    public static string DisplayName(this CloudProviderType type) => type switch
    {
        CloudProviderType.PCloud => "pCloud",
        CloudProviderType.KDrive => "kDrive",
        CloudProviderType.OneDrive => "OneDrive",
        CloudProviderType.GoogleDrive => "Google Drive",
        CloudProviderType.NextCloud => "NextCloud",
        CloudProviderType.ICloud => "iCloud",
        CloudProviderType.Koofr => "Koofr",
        CloudProviderType.Dropbox => "Dropbox",
        CloudProviderType.Mega => "Mega",
        CloudProviderType.WebDav => "WebDAV",
        CloudProviderType.Sftp => "SFTP",
        CloudProviderType.Ftp => "FTP",
        CloudProviderType.WordPress => "WordPress",
        CloudProviderType.GmxCloud => "GMX Cloud",
        CloudProviderType.S3 => "AWS S3",
        CloudProviderType.SynologyDrive => "Synology Drive",
        CloudProviderType.SynologyC2 => "Synology C2 Storage",
        CloudProviderType.S3Compatible => "S3-Compatible Storage",
        CloudProviderType.Box => "Box",
        CloudProviderType.Seafile => "Seafile",
        CloudProviderType.Filen => "Filen",
        CloudProviderType.Internxt => "Internxt",
        CloudProviderType.TeraBox => "TeraBox",
        CloudProviderType.Jottacloud => "Jottacloud",
        CloudProviderType.GoogleDrivePicker => "Google Drive (Selected Folders)",
        CloudProviderType.GoPro => "GoPro Camera",
        CloudProviderType.Qnap => "QNAP",
        CloudProviderType.Ugreen => "UGREEN NAS",
        CloudProviderType.Asustor => "ASUSTOR",
        _ => type.ToString(),
    };

    /// <summary>
    /// Import-only: media can be browsed, downloaded and deleted, but nothing can be created,
    /// renamed or uploaded. Hides the write affordances for a GoPro camera.
    /// </summary>
    public static bool IsReadOnly(this CloudProviderType type) => type == CloudProviderType.GoPro;

    /// <summary>WordPress' media library has no folders, and read-only devices have no writes.</summary>
    public static bool CanCreateFolders(this CloudProviderType type)
        => type != CloudProviderType.WordPress && !type.IsReadOnly();

    /// <summary>File name of the logo under the app's Assets/ProviderLogos folder.</summary>
    public static string LogoAssetName(this CloudProviderType type) => type switch
    {
        CloudProviderType.GoogleDrivePicker => "GoogleDrive",
        _ => type.ToString(),
    };

    /// <summary>
    /// Whether Add Account offers the type. TeraBox stays out of the list until TeraBox issues
    /// the Open Platform keys: without them sign-in can only fail (macOS 8503dad).
    /// </summary>
    public static bool IsOfferedInAddAccount(this CloudProviderType type)
        => type != CloudProviderType.TeraBox || Providers.TeraBox.TeraBoxProvider.IsAvailable;

    public static ProviderCategory Category(this CloudProviderType type) => type switch
    {
        CloudProviderType.Ftp or CloudProviderType.S3 or CloudProviderType.S3Compatible or
            CloudProviderType.Sftp or CloudProviderType.WebDav or
            CloudProviderType.WordPress => ProviderCategory.OtherProtocols,
        CloudProviderType.GoPro => ProviderCategory.Devices,
        CloudProviderType.SynologyDrive or CloudProviderType.Qnap or CloudProviderType.Ugreen or
            CloudProviderType.Asustor => ProviderCategory.Nas,
        _ => ProviderCategory.CloudStorage,
    };
}
