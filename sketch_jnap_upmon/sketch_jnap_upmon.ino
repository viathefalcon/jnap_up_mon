/*
Main program for autonomously monitoring that the WiFi is still up.
*/

// Includes
//
#include <SPI.h>
#include <WiFiNINA.h>
#include <WiFiUdp.h>
#include <RTCZero.h>

// Of the form:
// char ssid[] = "Access Point Name";
// char pass[] = "Access Point Password";
#include "credentials.h"

// Functions
//

int Jnap(char* action) {

  // Serial.print( "JNAP: " );
  // Serial.println( action );

  int count = 0;
  WiFiClient client;
  if (client.connect("192.168.1.1", 80)){
    client.println("POST /JNAP/ HTTP/1.1");
    client.println("Host: 192.168.1.1");
    client.println( "Accept: */*" );
    client.print( "X-JNAP-Action: " );
    client.println( action );
    client.println( "X-JNAP-Authorization: Basic YWRtaW46Rk00M2ImVDU5Rjd2" );
    client.println( "Content-Type: application/json; charset=UTF-8" );
    client.println( "Content-Length: 2" );
    client.println( );
    client.println( "{}" );
    client.println("Connection: close");
    client.println();

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
      //// Serial.println(line);
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
  return Jnap("http://linksys.com/jnap/core/Reboot");
}

void setup() {
  /*
  //Initialize serial and wait for port to open:
  Serial.begin(9600);
  while (!Serial) {
    ; // wait for serial port to connect. Needed for native USB port only
  }
  */

  // initialize digital pin LED_BUILTIN as an output, and immediately turn off
  pinMode(LED_BUILTIN, OUTPUT);
  digitalWrite(LED_BUILTIN, LOW);

  // check for the WiFi module:
  if (WiFi.status() == WL_NO_MODULE) {
    // Serial.println("Communication with WiFi module failed!");
    // don't continue
    while (true);
  }

  String fv = WiFi.firmwareVersion();
  if (fv < WIFI_FIRMWARE_LATEST_VERSION) {
    // Serial.println("Please upgrade the firmware");
    fv = "";
  }

  // Setup completed; turn the light back on
  digitalWrite(LED_BUILTIN, HIGH);
}

int ReadFromCanary() {

  const char* host = "www.google.com";

  int count = 0;
  WiFiClient client;
  if (client.connect(host, 80)){
    client.println("GET / HTTP/1.1");
    client.print("Host: ");
    client.println( host );
    client.println("Connection: close");
    client.println();

    // Get the response
    while (client.connected( )) {
      String line = client.readStringUntil('\n');
      if (line == "\r") {
        break;
      }
      //// Serial.println( line );
    }

    // Read it
    while (client.available( )){
      char c = client.read( );
      count += 1;
      //// Serial.print( c );
    }
    client.stop();
  }else{
    // Serial.print( "Failed to connect to " );
    // Serial.println( host );
  }
  return count;
}

void loop() {

  // Try and connect to the wifi network
  int status = WL_IDLE_STATUS;
  for (int counter = 0; (counter < 3) && (status != WL_CONNECTED); ++counter ){
    delay( 10000 );

    // Serial.println("Attempting to connect to WiFi..");
    status = WiFi.begin( ssid, pass );
  }
  if (status == WL_CONNECTED){
    // Serial.print( "Connected to " );
    // Serial.println( (char*) ssid );

    // Try and make an outbound HTTP call
    int read = ReadFromCanary( );
    // Serial.print( "Recv'd: " );
    // Serial.println( String( read ) );
    if (read < 1){
      read = RebootWifi( );
      // Serial.print( "Recv'd: " );
      // Serial.println( String( read ) );

      // For now, just turn off the light
      digitalWrite( LED_BUILTIN, LOW );
    }

    // Disconnect from the wifi
    WiFi.end( );
  }

  // Wait
  delay( 3600000 );
}
