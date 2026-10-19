using System.Runtime.InteropServices;
using PhotoGallery.Core.Cloud;
using Windows.Security.Credentials;

namespace PhotoGallery.App.Native;

/// <summary>Keeps the cloud sign-ins in Windows' credential store, encrypted for this Windows user.</summary>
internal static class CloudCredentials
{
    private const string User = "refresh-token";

    private static string Resource(CloudService service) => service switch
    {
        CloudService.GooglePhotos => "HolidayPhotoGallery Google Photos",
        _ => "HolidayPhotoGallery OneDrive",
    };

    public static string? Load(CloudService service)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource(service), User);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (COMException)
        {
            // Nothing saved yet.
            return null;
        }
    }

    public static void Save(CloudService service, string refreshToken)
    {
        Remove(service);
        new PasswordVault().Add(new PasswordCredential(Resource(service), User, refreshToken));
    }

    public static void Remove(CloudService service)
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var credential in vault.FindAllByResource(Resource(service)))
                vault.Remove(credential);
        }
        catch (COMException)
        {
        }
    }
}
