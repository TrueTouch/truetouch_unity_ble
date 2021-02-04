using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class TestGui : MonoBehaviour
{
    public TrueTouchBLE trueTouchBLE;

    public Text statusText;

    public Button executeButton;

    public Toggle thumbPulseToggle;
    public Toggle indexPulseToggle;
    public Toggle middlePulseToggle;
    public Toggle ringPulseToggle;
    public Toggle pinkyPulseToggle;

    public Toggle thumbERMToggle;
    public Toggle indexERMToggle;
    public Toggle middleERMToggle;
    public Toggle ringERMToggle;
    public Toggle pinkyERMToggle;
    public Toggle palmERMToggle;

    public InputField pulseDurationMsField;
    public InputField ermIntensityField;

    private List<Toggle> pulseToggles = new List<Toggle>();
    private List<Toggle> ermToggles = new List<Toggle>();

    // Start is called before the first frame update
    void Start()
    {
        //trueTouchBLE = GameObject.FindGameObjectWithTag("TrueTouchBLE").GetComponent<TrueTouchBLE>();

        /* Add all the toggles to a list for easier use down the road. */
        pulseToggles.Add(thumbPulseToggle);
        pulseToggles.Add(indexPulseToggle);
        pulseToggles.Add(middlePulseToggle);
        pulseToggles.Add(ringPulseToggle);
        pulseToggles.Add(pinkyPulseToggle);

        ermToggles.Add(thumbERMToggle);
        ermToggles.Add(indexERMToggle);
        ermToggles.Add(middleERMToggle);
        ermToggles.Add(ringERMToggle);
        ermToggles.Add(pinkyERMToggle);
        ermToggles.Add(palmERMToggle);
    }

    // Update is called once per frame
    void Update()
    {
        /* Update UI based on BLE state */
        switch (trueTouchBLE.GetState()) {
            case TrueTouchBLE.BleState.IDLE:
                statusText.text = "Idle";
                executeButton.interactable = false;
                break;

            case TrueTouchBLE.BleState.SCANNING:
                statusText.text = "Scanning";
                executeButton.interactable = false;
                break;

            case TrueTouchBLE.BleState.CONNECTING:
                statusText.text = "Connecting";
                executeButton.interactable = false;
                break;

            case TrueTouchBLE.BleState.CONNECTED:
                string newText = "Connected to: " + trueTouchBLE.GetConnectedDeviceName() + " {" + trueTouchBLE.GetConnectedDeviceID() + "}";
                statusText.text = newText;
                executeButton.interactable = true;
                break;

            case TrueTouchBLE.BleState.ERROR:
                statusText.text = "Error";
                executeButton.interactable = false;
                break;
        }
    }

    public void onExecute() {
        /* Grab input field values */
        UInt32 pulseDurMs = UInt32.Parse(pulseDurationMsField.text);
        byte ermIntensity = byte.Parse(ermIntensityField.text);

        Debug.Log("Parsed: pulse=" + pulseDurMs + " erm=" + ermIntensity);

        /* Update the pulse duration */
        trueTouchBLE.SetPulseDurationMs(pulseDurMs);

        /* Queue all the updates to happen. Note this relies on the fact that
         * items are entered into these lists in the same order as the Finger enum
         * in TrueTouchBLE. I.e. if "thumb" is the first finger, the thumb toggle
         * must be the first item added to the list. */
        for (int i = 0; i < pulseToggles.Count; ++i) {
            if (pulseToggles[i].isOn) {
                trueTouchBLE.UpdateFinger((TrueTouchBLE.Finger) i, TrueTouchBLE.UpdateType.PULSE_SOLENOID);
            }
        }

        for (int i = 0; i < ermToggles.Count; ++i) {
            if (ermToggles[i].isOn) {
                trueTouchBLE.UpdateFinger((TrueTouchBLE.Finger)i, TrueTouchBLE.UpdateType.SET_ERM, ermIntensity);
            }
        }
    }
}

