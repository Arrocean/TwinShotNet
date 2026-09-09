using UnityEngine;

namespace TwinShotNet;

public sealed partial class Plugin
{
    private string _address = "127.0.0.1", _port = "27020", _password = "", _fingerprint;
    private float _snapshotHz;

    private void LoadNetworkConfiguration()
    {
        _address = Config.Bind("Network", "Address", "127.0.0.1", "Host public IP or DNS name").Value;
        _port = Config.Bind("Network", "Port", 27020, "UDP listen/destination port").Value.ToString();
        _password = Config.Bind("Network", "RoomKey", "", "Use a long unique room key; UDP transport is not encrypted")
            .Value;
        _snapshotHz =
            Mathf.Clamp(Config.Bind("Network", "SnapshotHz", 30, "Full visual snapshots per second (10-60)").Value, 10,
                60);
    }
}