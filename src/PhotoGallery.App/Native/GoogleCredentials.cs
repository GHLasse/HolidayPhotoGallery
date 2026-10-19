using System.Runtime.InteropServices;
using Windows.Security.Credentials;

namespace PhotoGallery.App.Native;

/// <summary>Keeps the Google sign-in in Windows' credential store, encrypted for this Windows user.</summary>
internal static class GoogleCredentials
{
    private const string Resource = "HolidayPhotoGallery Google Photos";
    private const string User = "refresh-token";

    public static string? Load()
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, User);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (COMException)
        {
            // Nothing saved yet.
            return null;
        }
    }

    public static void Save(string refreshToken)
    {
        Remove();
        new PasswordVault().Add(new PasswordCredential(Resource, User, refreshToken));
    }

    public static void Remove()
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var credential in vault.FindAllByResource(Resource))
                vault.Remove(credential);
        }
        catch (COMException)
        {
        }
    }
}
