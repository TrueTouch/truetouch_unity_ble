﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

public class TrueTouchBLE : MonoBehaviour {
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
        ACTUATE_SOLENOID,
        RELEASE_SOLENOID,
        SET_ERM
    }

    private enum BleState {
        IDLE,
        SCANNING,
        CONNECTING,
        CONNECTED,
        ERROR
    }

    private struct BleUpdate {
        public BleApi.BLEData solenoidActuateData;
        public BleApi.BLEData solenoidReleaseData;
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
    private HashSet<Finger> actuateSolenoidUpdates = new HashSet<Finger>();
    private HashSet<Finger> releaseSolenoidUpdates = new HashSet<Finger>();
    private Dictionary<byte, HashSet<Finger>> ermUpdates = new Dictionary<byte, HashSet<Finger>>();

    // Start is called before the first frame update
    void Start() {
        /* Start scanning */
        BleApi.StartDeviceScan();
        state = BleState.SCANNING;
        Debug.Log("Scanning for devices");
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

        if ((actuateSolenoidUpdates.Count > 0 ||
            releaseSolenoidUpdates.Count > 0 ||
            ermUpdates.Count > 0) && !isWritingBle) {
            executeHandUpdate();
        }
    }

    /** Queue an update to be sent to the remote device. All queued updates are formatted
     *  into a BLE message and sent at the start of each frame. */
    public void UpdateFinger(Finger finger, UpdateType type, byte pwm = 0) {
        switch (type) {
            case UpdateType.ACTUATE_SOLENOID:
                /* If this finger is queued to be released, negate that */
                if (releaseSolenoidUpdates.Contains(finger)) {
                    releaseSolenoidUpdates.Remove(finger);
                }
                actuateSolenoidUpdates.Add(finger);
                break;

            case UpdateType.RELEASE_SOLENOID:
                /* If this finger is queued to be actuated, negate that */
                if (actuateSolenoidUpdates.Contains(finger)) {
                    actuateSolenoidUpdates.Remove(finger);
                }
                releaseSolenoidUpdates.Add(finger);
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
        // we shouldn't ever get data anyways
        BleApi.BLEData data = new BleApi.BLEData();
        while (BleApi.PollData(out data, false)) {
            string log_str = string.Format(
                "Recieved from {0} | {1} | {2}\n\t{3} bytes:\n\t",
                data.deviceId, data.serviceUuid, data.characteristicUuid, data.size
            );
            for (int i = 0; i < data.size; ++i) {
                log_str += data.buf[i].ToString("X2") + " ";
                if (i % 17 == 16) {
                    log_str += "\n\t";
                }
            }

            Debug.Log(log_str);
        }
    }

    private void handleBleError() {
        Debug.Log("Encountered Error: " + errorString);
        errorString = "None";
        state = BleState.IDLE;
    }

    private void bitsetToBytes(ref byte[] buffer, int startIdx, UInt32 bitset) {
        buffer[startIdx + 0] = (byte)((bitset & 0xFF000000) >> 24);
        buffer[startIdx + 1] = (byte)((bitset & 0x00FF0000) >> 16);
        buffer[startIdx + 2] = (byte)((bitset & 0x0000FF00) >> 8);
        buffer[startIdx + 3] = (byte)((bitset & 0x000000FF) >> 0);
    }

    private BleApi.BLEData formatSolenoidActuate() {
        BleApi.BLEData data = new BleApi.BLEData();
        data.size = 0;

        UInt32 solenoidFingerBitset = 0;
        foreach (Finger finger in actuateSolenoidUpdates) {
            solenoidFingerBitset |= (1U << (int)finger);
        }

        actuateSolenoidUpdates.Clear();

        if (solenoidFingerBitset == 0) {
            return data;
        }

        data.buf = new byte[512];
        data.size = 6;
        data.deviceId = truetouchDevice.id;
        data.serviceUuid = SERVICE_UUID;
        data.characteristicUuid = RX_CHAR_UUID;

        /** TrueTouch protocol - solenoid write format:
         *      [1 byte]  Command
         *      [4 bytes] Finger bitset
         *      [1 byte]  GpioOutput
         */
        data.buf[0] = (byte)TruetouchCommand.SOLENOID_WRITE;
        bitsetToBytes(ref data.buf, 1, solenoidFingerBitset);
        data.buf[5] = (byte)TruetouchGpioOutput.OUT_HIGH;

        return data;
    }

    private BleApi.BLEData formatSolenoidRelease() {
        BleApi.BLEData data = new BleApi.BLEData();
        data.size = 0;

        UInt32 solenoidFingerBitset = 0;
        foreach (Finger finger in releaseSolenoidUpdates) {
            solenoidFingerBitset |= (1U << (int)finger);
        }

        releaseSolenoidUpdates.Clear();

        if (solenoidFingerBitset == 0) {
            return data;
        }

        data.buf = new byte[512];
        data.size = 6;
        data.deviceId = truetouchDevice.id;
        data.serviceUuid = SERVICE_UUID;
        data.characteristicUuid = RX_CHAR_UUID;

        /** TrueTouch protocol - solenoid write format:
         *      [1 byte]  Command
         *      [4 bytes] Finger bitset
         *      [1 byte]  GpioOutput
         */
        data.buf[0] = (byte)TruetouchCommand.SOLENOID_WRITE;
        bitsetToBytes(ref data.buf, 1, solenoidFingerBitset);
        data.buf[5] = (byte)TruetouchGpioOutput.OUT_LOW;

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
            bitsetToBytes(ref data.buf, 1, ermFingerBitset);
            data.buf[5] = entry.Key;

            string dbg_str = "Sending: ";
            for (int i = 0; i < data.size; ++i) {
                dbg_str += data.buf[i].ToString("X2") + " ";
                if (i % 17 == 16) {
                    dbg_str += "\n\t";
                }
            }
            Debug.Log(dbg_str);

            ermSetCommands.Add(data);
        }

        ermUpdates.Clear();

        return ermSetCommands;
    }

    private void sendBleMessages(object obj) {
        BleUpdate update;
        try {
            update = (BleUpdate)obj;
        } catch (InvalidCastException) {
            isWritingBle = false;
            return;
        }

        /* Send all the data in sequence, blocking each time */

        if (update.solenoidActuateData.size > 0) {
            BleApi.SendData(in update.solenoidActuateData, true);
        }

        if (update.solenoidReleaseData.size > 0) {
            BleApi.SendData(in update.solenoidReleaseData, true);
        }

        if (update.ermSetData != null) {
            foreach (BleApi.BLEData data in update.ermSetData) {
                BleApi.SendData(in data, true);
            }   
        }

        isWritingBle = false;
    }

    private void executeHandUpdate() {
        isWritingBle = true; // starting series of BLE writes

        BleUpdate update = new BleUpdate();
        update.solenoidActuateData = formatSolenoidActuate();
        update.solenoidReleaseData = formatSolenoidRelease();
        update.ermSetData = formatErmSet();

        var th = new Thread(sendBleMessages);
        th.Start(update);
    }
}