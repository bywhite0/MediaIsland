namespace MediaIsland.Helpers
{
    public static class AppInfoHelper
    {
        
        private const string SpotifyPackagedAUMID = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify";
        private const string SpotifyUnpackagedAUMID = "Spotify.exe";

        public static bool IsSourceAppSpotify(string userModelId)
        {
            return string.Equals(userModelId, SpotifyPackagedAUMID)
            || string.Equals(userModelId, SpotifyUnpackagedAUMID);
        }
    }
}
