using NAudio.CoreAudioApi;

namespace ReceiptCapture.Services;

/// <summary>A microphone the user can choose. An empty Id means "whatever Windows is using by default".</summary>
public sealed record AudioDevice(string Id, string Name)
{
    public static readonly AudioDevice Default = new("", "Default microphone");
}

public static class AudioDevices
{
    /// <summary>The default entry first, then every microphone that is plugged in and enabled.</summary>
    public static List<AudioDevice> GetMicrophones()
    {
        var list = new List<AudioDevice> { AudioDevice.Default };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                list.Add(new AudioDevice(device.ID, device.FriendlyName));
                device.Dispose();
            }
        }
        catch (Exception)
        {
            // If Windows won't list devices, the default entry still works.
        }

        return list;
    }
}
