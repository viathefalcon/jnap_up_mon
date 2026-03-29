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

#define FLAG_SKIP_WAIT      0x01
#define FLAG_SKIP_CANARY    (FLAG_SKIP_WAIT << 1)

#define ONE_MINUTE_MILLIS   60000

// Globals
//

// Declare the service ID
BLEService bleService("505F8A1F-3872-449E-9167-B3549A5D7A87");

// Declare the ID for the characteristic by which a client invoke connect/read/reboot procedure
BLEByteCharacteristic runCharacteristic("E2C0FF71-A900-434D-9C39-6465443F3F5A", BLEWrite);

// Declare the ID for the characteristic by which a client invoke reboot procedure
BLEByteCharacteristic rebootCharacteristic("143E8851-01C0-49ED-8F37-9D287B6B32C7", BLEWrite);

// Declare the ID for the characteristic by which a client can get the time, in milliseconds,
// since the last time we asked the AP to reboot itself
BLEUnsignedLongCharacteristic mrrCharacteristic("43ADDD14-843B-407C-9B40-696E3819B4AE", BLERead);

// Gives the timestamp of the last iteration, and the last reboot, respectively
unsigned long mru = 0, mrr = 0;

// Gives the next operation to perform in the main loop
unsigned int flags = 0;

// Functions
//

int Jnap(char* action) {

  Serial.print( "JNAP: " );
  Serial.println( action );

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
      Serial.println(line);
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
      // Serial.println( line );
    }

    // Read it
    while (client.available( )){
      char c = client.read( );
      count += 1;
      // Serial.print( c );
    }
    // Serial.println( );
    client.stop( );
  }else{
    Serial.print( "Failed to connect to " );
    Serial.println( host );
  }
  return count;
}

int ReadFromCanaries() {

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

int ConnectToWiFi() {
  const unsigned long interval = 500;
  const unsigned long timeout  = 15000;

  int status = WiFi.status();
  for (int attempt = 0; (attempt < 3) && (status != WL_CONNECTED); ++attempt) {
    if (attempt > 0) {
      // Tear down the previous failed attempt cleanly before re-issuing begin()
      WiFi.disconnect();
      delay(2000);
    }

    Serial.print("Attempting to connect to ");
    Serial.print(ssid);
    Serial.print(" (attempt ");
    Serial.print(attempt + 1);
    Serial.println(")");
    WiFi.begin(ssid, pass);

    // Poll until associated or the per-attempt deadline expires
    const unsigned long deadline = millis() + timeout;
    do {
      delay(interval);
      status = WiFi.status();
      Serial.print("  status: ");
      Serial.println(status, HEX);
    } while ((status != WL_CONNECTED) && (millis() < deadline));
  }

  return status;
}

void RestartBLE() {
  do {
    delay(200);
  } while (!BLE.begin());

  BLE.setLocalName("Arduino");
  BLE.setAdvertisedService(bleService);

  bleService.addCharacteristic(runCharacteristic);
  bleService.addCharacteristic(rebootCharacteristic);
  bleService.addCharacteristic(mrrCharacteristic);

  BLE.addService(bleService);
  BLE.advertise();
}

void StopBLE() {
  // Tear down the BLE stack before handing the radio to WiFi.
  // stopAdvertise() prevents new connections; disconnect() cleanly closes any
  // active central connection (no-op if none); end() shuts the HCI layer down.
  BLE.stopAdvertise();
  BLE.disconnect();
  BLE.end();
}

void DoJnapUpMon(bool skipCanary) {
  StopBLE();

  const int status = ConnectToWiFi();
  if (status == WL_CONNECTED){
    // Try and make an outbound HTTP call
    int read = 0;
    if (!skipCanary){
      read = ReadFromCanaries( );
      Serial.print( "Recv'd: " );
      Serial.println( String( read ) );
    }
    if (read < 1){
      read = RebootWifi( );
      Serial.print( "Recv'd: " );
      Serial.println( String( read ) );

      // For now, just turn off the light
      digitalWrite( LED_BUILTIN, LOW );
    }

    // Disassociate cleanly, then power down the radio
    WiFi.disconnect();
    WiFi.end();
  }

  // Capture the timestamp and bring the BLE stack back up before returning
  mru = millis( );
  RestartBLE( );
}

void SetupWiFi() {
  // Wait for the WiFi module to come up
  if (WiFi.status() == WL_NO_MODULE) {
    Serial.println("Communication with WiFi module failed!");
    // don't continue
    while (true) {
      // Blink to signal fatal error
      digitalWrite(LED_BUILTIN, HIGH);
      delay(200);
      digitalWrite(LED_BUILTIN, LOW);
      delay(200);
    }  
  }

  String fv = WiFi.firmwareVersion( );
  if (fv < WIFI_FIRMWARE_LATEST_VERSION) {
    Serial.println("Please upgrade the firmware");
    fv = "";
  }
}

void runCharacteristicWritten(BLEDevice central, BLECharacteristic characteristic) {
  // Unused parameters
  (void)central;
  (void)characteristic;

  // central wrote new value to characteristic, update LED
  Serial.print("runCharacteristicWritten: ");
  if (runCharacteristic.value( )){
    Serial.println("non-zero.");

    // Set the flag
    flags |= FLAG_SKIP_WAIT;

    // Reset
    runCharacteristic.setValue( 0 );
  }else{
    Serial.println("zero.");
  }
}

void rebootCharacteristicWritten(BLEDevice central, BLECharacteristic characteristic) {
  // Unused parameters
  (void)central;
  (void)characteristic;

  // central wrote new value to characteristic, update LED
  Serial.print("rebootCharacteristicWritten: ");
  if (rebootCharacteristic.value( )){
    Serial.println("non-zero.");

    // Set the flag
    flags |= (FLAG_SKIP_WAIT | FLAG_SKIP_CANARY);

    // Reset
    rebootCharacteristic.setValue( 0 );
  }else{
    Serial.println("zero.");
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

  const unsigned long now = millis( );
  if (mrr >= now){
    // Clock has (probably) rolled over so we can't really know..
    mrrCharacteristic.writeValue( ULONG_MAX );
    return;
  }

  const unsigned long value = (now - mrr);
  Serial.print( "Calculated " );
  Serial.print( value, DEC );
  Serial.println( " as the delta since the last restart." );
  mrrCharacteristic.writeValue( value );
}

void onBLEConnected(BLEDevice central) {
  // central connected event
  Serial.print("Connected event, central: ");
  Serial.println(central.address( ));
}

void onBLEDisconnected(BLEDevice central) {
  BLE.advertise();
}

void SetupBLE() {
  // Initialise the characteristics
  runCharacteristic.setEventHandler(BLEWritten, runCharacteristicWritten);
  runCharacteristic.setValue(0);
  rebootCharacteristic.setEventHandler(BLEWritten, rebootCharacteristicWritten);
  rebootCharacteristic.setValue(0);
  mrrCharacteristic.setEventHandler(BLERead, mrrCharacteristicRead);

  // Start the connection
  RestartBLE();
}

void setup() {
  // Initialize serial
  Serial.begin(9600);

  // initialize digital pin LED_BUILTIN as an output, and immediately turn off
  pinMode(LED_BUILTIN, OUTPUT);
  digitalWrite(LED_BUILTIN, LOW);

  // Configure the WiFi stack
  SetupWiFi( );

  // Configure for Bluetooth
  SetupBLE( );

  // Tee up the first run by raising the flag
  flags |= FLAG_SKIP_WAIT;

  // Setup completed; turn the light back on
  digitalWrite(LED_BUILTIN, HIGH);
}

void loop() {
  // Get out the flags
  const bool skipWait = ((flags & FLAG_SKIP_WAIT) == FLAG_SKIP_WAIT);
  const bool skipCanary = ((flags & FLAG_SKIP_CANARY) == FLAG_SKIP_CANARY);
  Serial.print("flags = ");
  Serial.print( flags, HEX );
  Serial.print( "; skipWait = " );
  Serial.print( skipWait );
  Serial.print( "; skipCanary = " );
  Serial.println( skipCanary );
  flags = 0;

  // When did we get here - was it because of a timeout
  // or maybe we were told to just go
  const unsigned long interval = (5 * ONE_MINUTE_MILLIS);
  const unsigned long now = millis( );
  const unsigned long elapsed = (skipWait)
    ? (interval + 1)
    : (now < mru) ? interval : (now - mru);

  Serial.print( "Now = " );
  Serial.print( now, DEC );
  Serial.print( "; mru = " );
  Serial.println( mru, DEC );

  if (elapsed >= interval){
    // The clock has either rolled over or the timeout elapsed,
    // so do the thing
    DoJnapUpMon(skipCanary);
  }

  // Wait, poll for events from the Bluetooth stack
  const unsigned long timeout = (elapsed < interval) ? (interval - elapsed) : interval;
  BLE.poll( timeout );
}
