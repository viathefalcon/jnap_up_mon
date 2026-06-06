# JNAP UpMon

An Arduino sketch, for the [Arduino Nano 33 IoT](https://docs.arduino.cc/hardware/nano-33-iot/), to detect and resolve Internet connection drops, on routers which expose the JNAP API, by making regular requests to well-known, high-uptime web sites and in the case where such a connection can't be made bouncing the router by calling the `Reboot` API endpoint.

The sketch additionally implements a remote interface with Bluetooth Low Energy to enable companion app(s) to monitor, and manage, it.
