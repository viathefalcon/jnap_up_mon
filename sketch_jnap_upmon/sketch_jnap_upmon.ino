/*
Main program for autonomously monitoring that the WiFi is still up.
*/

// Includes
//
#include <limits.h>
#include <SPI.h>
#include <WiFiNINA.h>
#include <WiFiUdp.h>
#include <RTCZero.h>
#include <ArduinoBLE.h>

// Of the form:
// char ssid[] = "Access Point Name";
// char pass[] = "Access Point Password";
// char token[] = "Access Point Token"
#include "credentials.h"

// Macros
//

#define STATE_STARTING      0x00
#define STATE_POLLING       0x01
#define STATE_CONNECTING    0x02
#define STATE_READING       0x03
#define STATE_REBOOTING     0x04

// Globals
//

// Declare the service ID
BLEService bleService("505F8A1F-3872-449E-9167-B3549A5D7A87");

// Declare the ID for the characteristic by which a client invoke connect/read/reboot procedure
BLEByteCharacteristic runCharacteristic("E2C0FF71-A900-434D-9C39-6465443F3F5A", BLEWrite);

// Declare the ID for the characteristic by which a client invoke reboot procedure
BLEByteCharacteristic rebootCharacteristic("143E8851-01C0-49ED-8F37-9D287B6B32C7", BLEWrite);

// Declare the ID for the characteristic by which a client can get the most recent state
BLEByteCharacteristic stateCharacteristic("8186E6A2-77A6-43CC-8C99-31DC36136147", BLERead | BLENotify);

// Declare the ID for the characteristic by which a client can get the time, in milliseconds,
// since the last time we asked the AP to reboot itself
BLEUnsignedLongCharacteristic mrrCharacteristic("43ADDD14-843B-407C-9B40-696E3819B4AE", BLERead);

// Gives the timestamp of the last iteration, and the last reboot, respectively
unsigned long mru = 0, mrr = 0;

// Functions
//

int Jnap(char* action) {

  //Serial.print( "JNAP: " );
  //Serial.println( action );

  int count = 0;
  WiFiClient client;
  if (client.connect("192.168.1.1", 80)){
    client.println("POST /JNAP/ HTTP/1.1");
    client.println("Host: 192.168.1.1");
    client.println( "Accept: */*" );
    client.print( "X-JNAP-Action: " );
    client.println( action );
    client.print( "X-JNAP-Authorization: Basic " );
    client.println( token );
    client.println( "Content-Type: application/json; charset=UTF-8" );
    client.println( "Content-Length: 2" );
    client.println( );
    client.println( "{}" );
    client.println("Connection: close");
    client.println( );

    // Read the response
    while (client.connected( )) {
      String line = client.readStringUntil('\n');
      if (line == "\r") {
        break;
      }
    }

    // Print the response
    while (client.available( )) {
      String line = client.readStringUntil('\n');
      count += line.length( );
      //Serial.println(line);
    }

    // Cleanup, get out
    client.stop( );
  }
  return count;
}

int GetWANStatus3() {
  return Jnap("http://linksys.com/jnap/router/GetWANStatus3");
}

int RebootWifi() {
  // Ask the AP to reboot
  stateCharacteristic.writeValue( STATE_REBOOTING );
  int result = Jnap("http://linksys.com/jnap/core/Reboot");

  // Capture the timestamp before returning
  mrr = millis( );
  return result;
}

int ReadFromCanary(const char* host, const char* path) {

  int count = 0;
  WiFiClient client;
  if (client.connect(host, 80)){
    client.print("GET ");
    client.print( path );
    client.println(" HTTP/1.1");
    client.print("Host: ");
    client.println( host );
    client.println("Accept: */*");
    client.println("Connection: close");
    client.println( );

    // Get the response
    while (client.connected( )) {
      String line = client.readStringUntil('\n');
      if (line == "\r") {
        break;
      }
      //Serial.println( line );
    }

    // Read it
    while (client.available( )){
      char c = client.read( );
      count += 1;
      // //Serial.print( c );
    }
    // //Serial.println( );
    client.stop( );
  }else{
    //Serial.print( "Failed to connect to " );
    //Serial.println( host );
  }
  return count;
}

int ReadFromCanaries() {

  stateCharacteristic.writeValue( STATE_READING );
  int count = ReadFromCanary(
    "www.msftconnecttest.com",
    "/connecttest.txt"
  );
  if (count < 1){
    // Fallback to Google's home page,
    // which shouldn't orindarily
    // require a re-try?
    count = ReadFromCanary(
      "www.google.com",
      "/"
    );
  }
  return count;
}

void DoJnapUpMon() {
  stateCharacteristic.writeValue( STATE_CONNECTING );

  // Try and connect to the wifi network
  int status = WL_IDLE_STATUS;
  for (int counter = 0; (counter < 3) && (status != WL_CONNECTED); ++counter ){
    delay( 10000 );

    //Serial.print("Attempting to connect to ");
    //Serial.print( ssid );
    //Serial.print( ": " );
    status = WiFi.begin( ssid, pass );
    //Serial.println(status, HEX);
  }
  if (status == WL_CONNECTED){
    // Try and make an outbound HTTP call
    int read = ReadFromCanaries( );
    //Serial.print( "Recv'd: " );
    //Serial.println( String( read ) );
    if (read < 1){
      read = RebootWifi( );
      //Serial.print( "Recv'd: " );
      //Serial.println( String( read ) );

      // For now, just turn off the light
      digitalWrite( LED_BUILTIN, LOW );
    }

    // Disconnect from the wifi
    WiFi.end( );
  }

  // Capture the timestamp
  mru = millis( );
}

void SetupWiFi() {
  // Wait for the WiFi module to come up
  if (WiFi.status() == WL_NO_MODULE) {
    //Serial.println("Communication with WiFi module failed!");
    // don't continue
    while (true);
  }

  String fv = WiFi.firmwareVersion( );
  if (fv < WIFI_FIRMWARE_LATEST_VERSION) {
    //Serial.println("Please upgrade the firmware");
    fv = "";
  }
}

void SetupBLE() {
  // Initialise the Bluetooth Low Energy elements
  if (!BLE.begin()) {
    //Serial.println("starting Bluetooth® Low Energy module failed!");
    while (true);
  }
  BLE.setLocalName("JnapUpMon");
  BLE.setAdvertisedService(bleService);

  // Add the characteristics to the service
  bleService.addCharacteristic(runCharacteristic);
  bleService.addCharacteristic(rebootCharacteristic);
  bleService.addCharacteristic(stateCharacteristic);
  bleService.addCharacteristic(mrrCharacteristic);

  // Add the service to the stack
  BLE.addService(bleService);

  // Initialise the characteristics
  runCharacteristic.setEventHandler(BLEWritten, runCharacteristicWritten);
  runCharacteristic.setValue(0);
  rebootCharacteristic.setEventHandler(BLEWritten, rebootCharacteristicWritten);
  rebootCharacteristic.setValue(0);
  stateCharacteristic.setValue( STATE_STARTING );
  mrrCharacteristic.setEventHandler(BLERead, mrrCharacteristicRead);

  // Finish up, start advertising the service
  BLE.advertise( );
}

void setup() {
  /*
  // Initialize serial and wait for port to open:
  Serial.begin(9600);
  while (!Serial) {
    ; // wait for serial port to connect. Needed for native USB port only
  }
  */

  // initialize digital pin LED_BUILTIN as an output, and immediately turn off
  pinMode(LED_BUILTIN, OUTPUT);
  digitalWrite(LED_BUILTIN, LOW);

  // Configure the WiFi stack
  SetupWiFi( );

  // Now that the WiFi is up, we can do the first run
  // before looking for BLE events
  DoJnapUpMon( );

  // Configure for Bluetooth
  SetupBLE( );

  // Setup completed; turn the light back on
  digitalWrite(LED_BUILTIN, HIGH);
}

void loop() {
  const unsigned long interval = 600000;
  const unsigned long now = millis( );
  //Serial.print( "Now = " );
  //Serial.print( now, DEC );
  //Serial.print( "; mru = " );
  //Serial.println( mru, DEC );

  // When did we get here - was it because of a timeout
  const unsigned long elapsed = (now < mru) ? interval : (now - mru);
  if (elapsed >= interval){
    // The clock has either rolled over or the timeout elapsed,
    // so do the thing
    DoJnapUpMon( );
  }

  // Wait, poll for events from the Bluetooth stack
  stateCharacteristic.writeValue( STATE_POLLING );
  const unsigned long timeout = (elapsed < interval) ? (interval - elapsed) : interval;
  BLE.poll( timeout );
}

void runCharacteristicWritten(BLEDevice central, BLECharacteristic characteristic) {
  // Unused parameters
  (void)central;
  (void)characteristic;

  // central wrote new value to characteristic, update LED
  //Serial.print("Characteristic event, written: ");
  if (runCharacteristic.value( )){
    //Serial.println("non-zero.");

    // Do the thing
    DoJnapUpMon( );

    // Reset
    runCharacteristic.setValue( 0 );
  }else{
    //Serial.println("non-zero.");
  }
}

void rebootCharacteristicWritten(BLEDevice central, BLECharacteristic characteristic) {
  // Unused parameters
  (void)central;
  (void)characteristic;

  // central wrote new value to characteristic, update LED
  //Serial.print("Characteristic event, written: ");
  if (rebootCharacteristic.value( )){
    //Serial.println("non-zero.");

    // Reboot the WiFi
    RebootWifi( );

    // Reset
    rebootCharacteristic.setValue( 0 );
  }else{
    //Serial.println("non-zero.");
  }
}

// Read callback fires when a central performs a GATT Read
void mrrCharacteristicRead(BLEDevice central, BLECharacteristic characteristic) {
  // Unused parameters
  (void)central;
  (void)characteristic;

  // Look for an early out
  if (mrr == 0){
    // Never
    mrrCharacteristic.writeValue( 0 );
    return;
  }

  const unsigned int now = millis( );
  if (mrr >= now){
    // Clock has (probably) rolled over so we can't really know..
    mrrCharacteristic.writeValue( ULONG_MAX );
    return;
  }

  const unsigned long value = (now - mrr);
  //Serial.print( "Calculated " );
  //Serial.print( value, DEC );
  //Serial.println( " as the delta since the last restart." );
  mrrCharacteristic.writeValue( value );
}
