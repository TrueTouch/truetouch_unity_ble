# truetouch_unity_ble
A Unity script to connect and interact with a TrueTouch device.
This script uses the BleWinrtDll API to interact over BLE from Unity.

# Using This Script in Unity
1. Copy the BleWinrtDll assets into your project's list of assets:
    * `BleWinrtDll/BleWinrtDll Unity/Assets/BleApi.cs`
    * `BleWinrtDll/BleWinrtDll Unity/Assets/BleWinrtDll.dll`
2. Copy the `TrueTouchBLE.cs` script into the list of assets
3. Add the `TrueTouchBLE.cs` script to an appropriate element in your scene
    * E.g. create a new, empty GameObject and add the script to it
4. To interact with the TrueTouch device, call the `TrueTouchBLE.UpdateFinger` method
    * This method will queue updates to be sent to the device
    * If a new update conflicts with an already queued one (e.g. an "actuate solenoid" update
      after a "release solenoid" update on the same finger), the more recent update will be 
      performed
    * Once the TrueTouch device is connected to, any queued updates will be sent to it once per frame
    * Connecting to the TrueTouch device is done automatically and robustly by an FSM that operates
      once per frame

# Example Unity Scene
An example Unity setup that can test the TrueTouch device is included in the `UnityBleTestGui` 
directory. To open the project, select "Add" from the Unity Hub Projects tab and select the `UnityBleTestGui` 
directory. 

