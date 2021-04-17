/* I have no idea how Unity/C# handles debugging, this might not make sense. */
//#define BLE_BENCHMARK

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

public class TrueTouchBLE : MonoBehaviour {
    /** Variable modifiable in the Unity environment or by setPulseDurationMs().
     *  How many ms to pulse solenoids for */
    public UInt32 SolenoidPulseDurationMs = 10;

    /** How often to push updates to the glove. Game must be stopped then restarted for updates
     *  to take place. */
    public float SendRateSeconds = 0.2f;

    /** Fingers that can be updated */
    public enum Finger {
        THUMB,
        INDEX,
        MIDDLE,
        RING,
        PINKY,
        PALM,
    }

    /** Types of updates for finger state */
    public enum UpdateType {
        PULSE_SOLENOID,
        SET_ERM
    }

    public enum BleState {
        IDLE,
        SCANNING,
        CONNECTING,
        CONNECTED,
        ERROR
    }

    private struct BleUpdate {
        public BleApi.BLEData solenoidPulseData;
        public List<BleApi.BLEData> ermSetData;
    }

    /** TrueTouch protocol - commands */
    private enum TruetouchCommand {
        SOLENOID_WRITE = 0x01,  /*!< Digital write to the given fingers' solenoids. */
        SOLENOID_PULSE = 0x02,  /*!< Pulse given fingers' solenoids for so many ms. */
        ERM_SET = 0x03,         /*!< Set PWM on given fingers' ERM motors. */
    };

    /** TrueTouch protocol - fingers */
    private enum TruetouchFinger {
        THUMB = 0,
        INDEX = 1,
        MIDDLE = 2,
        RING = 3,
        PINKY = 4,
        PALM = 5,
    };

    /** TrueTouch protocol - solenoid GPIO levels. */
    private enum TruetouchGpioOutput {
        OUT_LOW = 0,
        OUT_HIGH = 1
    };

    const string DEVICE_NAME = "TrueTouch";
    const string SERVICE_UUID = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
    const string RX_CHAR_UUID = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string TX_CHAR_UUID = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";

    private BleState state = BleState.IDLE;
    private BleApi.DeviceUpdate truetouchDevice = new BleApi.DeviceUpdate();
    private string errorString = "None";
    private bool isWritingBle = false;

    /** Updates to be sent out every frame */
    private HashSet<Finger> solenoidsToPulse = new HashSet<Finger>();
    private Dictionary<byte, HashSet<Finger>> ermUpdates = new Dictionary<byte, HashSet<Finger>>();

#if BLE_BENCHMARK
    /** Time previous message was sent. */
    private DateTime previousSendTime;
    private bool awaitingResponse = false;
#endif

    // Start is called before the first frame update
    void Start() {
        /* Start scanning */
        BleApi.StartDeviceScan();
        state = BleState.SCANNING;
        Debug.Log("Scanning for devices");

        InvokeRepeating("executeHandUpdate", SendRateSeconds, SendRateSeconds);
    }

    // Update is called once per frame
    void Update() {
        switch (state) {
            case BleState.IDLE:
                handleBleIdle();
                break;

            case BleState.SCANNING:
                handleBleScanning();
                break;

            case BleState.CONNECTING:
                handleBleConnecting();
                break;

            case BleState.CONNECTED:
                handleBleConnected();
                break;

            case BleState.ERROR:
                handleBleError();
                break;
        }
    }

    public BleState GetState() {
        return state;
    }

    public string GetConnectedDeviceName() {
        if (state == BleState.CONNECTED) {
            return truetouchDevice.name;
        } else {
            return "Disconnected";
        }
    }

    public string GetConnectedDeviceID() {
        if (state == BleState.CONNECTED) {
            return truetouchDevice.id;
        } else {
            return "Disconnected";
        }
    }

    public void SetPulseDurationMs(UInt32 pulseDuration) {
        SolenoidPulseDurationMs = pulseDuration;
    }

    /** Queue an update to be sent to the remote device. All queued updates are formatted
     *  into a BLE message and sent at the start of each frame. */
    public void UpdateFinger(Finger finger, UpdateType type, byte pwm = 0) {
        switch (type) {
            case UpdateType.PULSE_SOLENOID:
                solenoidsToPulse.Add(finger);
                break;

            case UpdateType.SET_ERM: {
                    HashSet<Finger> set;

                    /* If this finger was previously updated, negate that */
                    foreach (KeyValuePair<byte, HashSet<Finger>> entry in ermUpdates) {
                        if (entry.Value.Contains(finger) && entry.Key != pwm) {
                            entry.Value.Remove(finger);
                        } else if (entry.Value.Contains(finger)) {
                            /* Same finger, same PWM, nothing to be done */
                            return;
                        }
                    }

                    /* New finger or PWM */
                    if (ermUpdates.ContainsKey(pwm)) {
                        if (!ermUpdates.TryGetValue(pwm, out set)) {
                            errorString = "Couldn't get value from ermUpdates";
                            state = BleState.ERROR;
                        } else {
                            set.Add(finger); // don't care if it succeeds or not
                        }
                    } else {
                        set = new HashSet<Finger>();
                        set.Add(finger);
                        ermUpdates.Add(pwm, set);
                    }
                }
                break;
        }
    }

    private void handleBleIdle() {
        /* Entering here must mean an error happened; just try to restart */
        BleApi.StartDeviceScan();
        state = BleState.SCANNING;
        Debug.Log("Restart scanning for devices");
    }

    /** Search for the TrueTouch device. */
    private void handleBleScanning() {
        BleApi.DeviceUpdate device = new BleApi.DeviceUpdate();
        BleApi.ScanStatus status;
        do {
            status = BleApi.PollDevice(ref device, false);
            if (status == BleApi.ScanStatus.AVAILABLE) {
                Debug.Log("Found device: " + device.name + " | " + device.id);
                if (device.name.Equals(DEVICE_NAME, StringComparison.OrdinalIgnoreCase)) {
                    /* TrueTouch found, scan for the desired service UUID */
                    BleApi.StopDeviceScan();
                    truetouchDevice = device;
                    BleApi.ScanServices(truetouchDevice.id);
                    state = BleState.CONNECTING;
                    Debug.Log("Scanning TrueTouch's services");
                    return;
                }
            } else if (status == BleApi.ScanStatus.FINISHED) {
                /* Ran out of devices without finding target device */
                errorString = "Could not find TrueTouch";
                state = BleState.ERROR;
            }
        } while (status == BleApi.ScanStatus.AVAILABLE);
    }

    private void handleBleConnecting() {
        BleApi.Service service = new BleApi.Service();
        BleApi.ScanStatus status;
        do {
            status = BleApi.PollService(out service, false);
            if (status == BleApi.ScanStatus.AVAILABLE) {
                Debug.Log("Found service: " + service.uuid);
                string uuid = service.uuid.Replace("{", null).Replace("}", null);
                if (uuid.Equals(SERVICE_UUID, StringComparison.OrdinalIgnoreCase)) {
                    /* Service found, subscribe to updates from TX char */
                    BleApi.SubscribeCharacteristic(truetouchDevice.id, SERVICE_UUID, TX_CHAR_UUID, false);
                    state = BleState.CONNECTED;
                    Debug.Log("Connected to TrueTouch");
                    return;
                }
            } else if (status == BleApi.ScanStatus.FINISHED) {
                /* Couldn't find target service on the device */
                errorString = "Could not find NUS service on TrueTouch";
                state = BleState.ERROR;
            }
        } while (status == BleApi.ScanStatus.AVAILABLE);
    }

    private void handleBleConnected() {
        /* If we get data it's likely ACKs for benchmarking. */
        BleApi.BLEData data = new BleApi.BLEData();
        while (BleApi.PollData(out data, false)) {
            string log_str = string.Format(
                "Recieved from {0} | {1} | {2}\n\t{3} bytes:\n\t",
                data.deviceId, data.serviceUuid, data.characteristicUuid, data.size
            );
            Debug.Log(log_str);

            if (awaitingResponse) { /* Got response. */
                awaitingResponse = false;
                var now = DateTime.Now;
                var timediff = now - previousSendTime;
                Debug.Log(
                    string.Format("Received ACK in {0} ms", timediff.TotalMilliseconds)
                );
            }
        }
    }

    private void handleBleError() {
        Debug.Log("Encountered Error: " + errorString);
        errorString = "None";
        state = BleState.IDLE;
    }

    private void uint32ToBytes(ref byte[] buffer, int startIdx, UInt32 value) {
        buffer[startIdx + 0] = (byte)((value & 0xFF000000) >> 24);
        buffer[startIdx + 1] = (byte)((value & 0x00FF0000) >> 16);
        buffer[startIdx + 2] = (byte)((value & 0x0000FF00) >> 8);
        buffer[startIdx + 3] = (byte)((value & 0x000000FF) >> 0);
    }

    private BleApi.BLEData formatSolenoidPulse() {
        BleApi.BLEData data = new BleApi.BLEData();
        data.size = 0;

        UInt32 solenoidFingerBitset = 0;
        foreach (Finger finger in solenoidsToPulse) {
            solenoidFingerBitset |= (1U << (int)finger);
        }

        solenoidsToPulse.Clear();

        if (solenoidFingerBitset == 0) {
            return data;
        }

        data.buf = new byte[512];
        data.size = 9;
        data.deviceId = truetouchDevice.id;
        data.serviceUuid = SERVICE_UUID;
        data.characteristicUuid = RX_CHAR_UUID;

        /** TrueTouch protocol - solenoid pulse format:
         *      [1 byte]  Command
         *      [4 bytes] Finger bitset
         *      [4 bytes] Duration (ms)
         */
        data.buf[0] = (byte)TruetouchCommand.SOLENOID_PULSE;
        uint32ToBytes(ref data.buf, 1, solenoidFingerBitset);
        uint32ToBytes(ref data.buf, 1 + 4, SolenoidPulseDurationMs);

        return data;
    }

    private List<BleApi.BLEData> formatErmSet() {
        List<BleApi.BLEData> ermSetCommands = new List<BleApi.BLEData>();

        /* Each different PWM intensity must be sent in a separate message. */
        foreach (KeyValuePair<byte, HashSet<Finger>> entry in ermUpdates) {
            UInt32 ermFingerBitset = 0;
            foreach (Finger finger in entry.Value) {
                ermFingerBitset |= (1U << (int)finger);
            }

            BleApi.BLEData data = new BleApi.BLEData();
            data.buf = new byte[512];
            data.size = 6;
            data.deviceId = truetouchDevice.id;
            data.serviceUuid = SERVICE_UUID;
            data.characteristicUuid = RX_CHAR_UUID;

            /** TrueTouch protocol - ERM motor set format 
             *      [1 byte]  Command
             *      [4 bytes] Finger bitset
             *      [1 byte]  Intensity
             */
            data.buf[0] = (byte)TruetouchCommand.ERM_SET;
            uint32ToBytes(ref data.buf, 1, ermFingerBitset);
            data.buf[5] = entry.Key;

            ermSetCommands.Add(data);
        }

        ermUpdates.Clear();

        return ermSetCommands;
    }

    private void sendBleMessages(object obj) {
        BleUpdate update;
        try {
            update = (BleUpdate)obj;
        } catch (InvalidCastException) { // should never happen
            isWritingBle = false;
            return;
        }

        /* Send all the data in sequence, blocking each time */
        if (update.solenoidPulseData.size > 0) {
            BleApi.SendData(in update.solenoidPulseData, true);
        }

        if (update.ermSetData != null) {
            foreach (BleApi.BLEData data in update.ermSetData) {
                BleApi.SendData(in data, true);
            }
        }

        isWritingBle = false;
    }

    void executeHandUpdate() {
        /* Do nothing if already writing or if nothing to do */
        if (!(solenoidsToPulse.Count > 0 || ermUpdates.Count > 0) || isWritingBle) {
            return;
        }

        /* When debugging, throttle messages until we get an ACK */
#if BLE_BENCHMARK
        if (awaitingResponse) {
            return;
        }

        /* Setup flag and timing for ACK measurement. */
        previousSendTime = DateTime.Now;
        awaitingResponse = true;
#endif

        isWritingBle = true; // starting series of BLE writes

        BleUpdate update = new BleUpdate();
        update.solenoidPulseData = formatSolenoidPulse();
        update.ermSetData = formatErmSet();

        string debugStr = "Sending ";
        if (update.solenoidPulseData.size > 0) {
            debugStr += "solenoid pulse: " + update.solenoidPulseData.size + " bytes:\n\t";
            for (int i = 0; i < update.solenoidPulseData.size; ++i) {
                debugStr += update.solenoidPulseData.buf[i].ToString("X2") + " ";
                if (i % 17 == 16) {
                    debugStr += "\n\t";
                }
            }
            if (update.ermSetData.Count > 0) {
                debugStr += "\n and ";
            }
        }

        if (update.ermSetData.Count > 0) {
            debugStr += update.ermSetData.Count + " ERM sets:";
            foreach (BleApi.BLEData data in update.ermSetData) {
                debugStr += "\n\t";
                for (int i = 0; i < data.size; ++i) {
                    debugStr += data.buf[i].ToString("X2") + " ";
                    if (i % 17 == 16) {
                        debugStr += "\n\t";
                    }
                }
            }
        }

        Debug.Log(debugStr);

        var th = new Thread(sendBleMessages);
        th.Start(update);
    }
}
