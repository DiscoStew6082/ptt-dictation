namespace PttDictation.Core;

internal static class DictationSessionAudioOwnership
{
    public static string? Release(RecordedAudio? audio)
    {
        if (audio is not { DeleteAfterUse: true })
        {
            return null;
        }

        try
        {
            if (File.Exists(audio.Path))
            {
                File.Delete(audio.Path);
            }

            return null;
        }
        catch (IOException)
        {
            return audio.Path;
        }
        catch (UnauthorizedAccessException)
        {
            return audio.Path;
        }
    }
}
