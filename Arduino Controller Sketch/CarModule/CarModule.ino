#include <EEPROM.h>
#include <WiFi.h> 
#include <WiFiClient.h> 
#include <ArduinoBLE.h> 
#include "ArduinoGraphics.h"
#include "Arduino_LED_Matrix.h"
#include <WiFiSSLClient.h>
#include <RTC.h>

#define CRITICAL_BATTERY_LOW      15  // 15% Trigger threshold
#define SAFE_BATTERY_CEILING      35  // 35% Release threshold

String CLOUDFLARE_HOST     = "silent-bird-d9c0.taigon1984.workers.dev";
const uint16_t CLOUDFLARE_PORT  = 443;

String CF_CLIENT_ID        = "PASTE_YOUR_CF_ACCESS_CLIENT_ID_HERE";
String CF_CLIENT_SECRET    = "PASTE_YOUR_CF_ACCESS_CLIENT_SECRET_HERE";

bool lastCloudTransmitSuccessful = false; 
String globalCloudflareHeadersBuffer = "";
String lastAdminPayload = "";

String globalLastUploadedLogTimestamp = ""; 

const String DEFAULT_MASTER_PASSWORD = "VersaPasscode99"; 
const String DEFAULT_WIFI_AP_NAME = "Versa_Automation_Hub"; 
const String DEFAULT_BLE_NAME = "VersaHub_BLE";

uint8_t aesSecretKeyBytes[16] = {0x5A, 0xA5, 0x1F, 0x2C, 0x7E, 0x9D, 0x8B, 0x34, 0x61, 0xF0, 0xE3, 0xD2, 0xC1, 0xB0, 0x09, 0x48};
uint8_t initializationVector[16] = {0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F};

String decryptPayloadAES128CBC(const String& base64Input);
bool processSecureCommand(String rawPacket, String source);

const uint8_t CRYPTO_SALT_KEY[16] = {0x5A, 0xA5, 0x1F, 0x2C, 0x7E, 0x9D, 0x8B, 0x34, 0x61, 0xF0, 0xE3, 0xD2, 0xC1, 0xB0, 0x09, 0x48};
ArduinoLEDMatrix matrix; 

WiFiServer webServer(80);

String activeDashboardText = " START "; 

bool pendingSystemHardwareRebootFlag = false;
unsigned long hardwareRebootTimestampCount = 0;

const int RELAY_LOCK = 8;
const int RELAY_UNLOCK = 9;
const int RELAY_SOLENOID = 6;
const int RELAY_AMP_REM = 5;

const int VOLTAGE_FRONT = A1;
const int VOLTAGE_BACK = A3;
const int RADIO_SENSOR = 7; 

const float CALIBRATION_FRONT = 3.1276;
const float CALIBRATION_BACK = 3.0673;
const float ARDUINO_REF_VOLTAGE = 5.00;
unsigned long previousTelemetryMillis = 0;
const long telemetryInterval = 10000; 

const int EEPROM_LOCK_HASH_ADDR = 10; 
const int EEPROM_UNLOCK_HASH_ADDR = 12; 
const int EEPROM_INIT_FLAG_ADDR = 50; 
const int EEPROM_WIFI_SSID_ADDR = 60; 
const int EEPROM_WIFI_PASS_ADDR = 100;
const int EEPROM_CUSTOM_WIFI_AP = 140; 
const int EEPROM_CUSTOM_BLE_NAME = 180; 
const int EEPROM_CF_HOST_ADDR      = 250;
const int EEPROM_CF_CLIENT_ID_ADDR = 350;
const int EEPROM_CF_SECRET_ADDR    = 450;

const int MAX_SYSTEM_LOGS = 10;
String systemLogBufferArray[MAX_SYSTEM_LOGS];
int currentLogWritePointerIndex = 0;

unsigned int currentLockHash = 0;
unsigned int currentUnlockHash = 0;
String incomingBuffer = "";
float globalFrontVolts = 0;
float globalBackVolts = 0;
int frontBatteryPercent = 100;
int backBatteryPercent = 100;

float frontChargingVolts = 12.9;
float backChargingVolts = 13.50;

bool frontIsCharging = (globalFrontVolts >= frontChargingVolts);
bool backIsCharging = (globalBackVolts >= backChargingVolts);
bool audioOverride = false;

static int longRangeAdminSyncCounter = 999;    

static unsigned long lastCloudUploadTimestamp = 0;
static unsigned long rapidResponseWindowExpiration = 0;
unsigned long activeCloudPacingInterval = 10000;

bool crossChargeProtectionActiveFlag = false; 

String currentBroadcastAP = "";
String currentBroadcastBLE = "";

BLEService hubService("19B10000-E8F2-537E-4F1D-223A12345678");
BLEStringCharacteristic rxCharacteristic("19B10001-E8F2-537E-4F1D-223A12345678", BLEWrite, 512);
BLEStringCharacteristic txCharacteristic("19B10002-E8F2-537E-4F1D-223A12345678", BLENotify, 512);

void setup() {
    pinMode(RELAY_LOCK, OUTPUT); digitalWrite(RELAY_LOCK, HIGH);
    pinMode(RELAY_UNLOCK, OUTPUT); digitalWrite(RELAY_UNLOCK, HIGH);
    pinMode(RELAY_SOLENOID, OUTPUT); digitalWrite(RELAY_SOLENOID, HIGH); 
    pinMode(RELAY_AMP_REM, OUTPUT); digitalWrite(RELAY_AMP_REM, HIGH); 
    pinMode(RADIO_SENSOR, INPUT);
    
    Serial.begin(9600);
    matrix.begin(); 
    displayMatrixText(" START ");
    
    if (EEPROM.read(EEPROM_INIT_FLAG_ADDR) != 0xAA) {
        unsigned int initialHash = generateFletcher16Hash(DEFAULT_MASTER_PASSWORD);
        saveHashToEEPROM(EEPROM_LOCK_HASH_ADDR, initialHash);
        saveHashToEEPROM(EEPROM_UNLOCK_HASH_ADDR, initialHash);
        writeStringToEEPROM(EEPROM_CUSTOM_WIFI_AP, DEFAULT_WIFI_AP_NAME);
        writeStringToEEPROM(EEPROM_CUSTOM_BLE_NAME, DEFAULT_BLE_NAME);
        EEPROM.write(EEPROM_INIT_FLAG_ADDR, 0xAA);
    }
    
    currentLockHash = readHashFromEEPROM(EEPROM_LOCK_HASH_ADDR);
    currentUnlockHash = readHashFromEEPROM(EEPROM_UNLOCK_HASH_ADDR);
    
    currentBroadcastAP = readStringFromEEPROM(EEPROM_CUSTOM_WIFI_AP);
    currentBroadcastBLE = readStringFromEEPROM(EEPROM_CUSTOM_BLE_NAME);

    CLOUDFLARE_HOST   = readStringFromEEPROM(EEPROM_CF_HOST_ADDR);
    CF_CLIENT_ID      = readSecureStringFromEEPROM(EEPROM_CF_CLIENT_ID_ADDR);
    CF_CLIENT_SECRET  = readSecureStringFromEEPROM(EEPROM_CF_SECRET_ADDR);

    if (CLOUDFLARE_HOST.length() == 0) {
        CLOUDFLARE_HOST = "silent-bird-d9c0.taigon1984.workers.dev";
    }

    setupBluetoothNetwork();
    setupWiFiAPI();

    displayMatrixText(" HUB ONLINE ");
}

void loop() {
    unsigned long currentMillis = millis();
    
    handleWiFiAPI();
    BLE.poll();
    
    if (BLE.connected() && rxCharacteristic.written()) {
        incomingBuffer = rxCharacteristic.value();
        processSecureCommand(incomingBuffer, "BLE_LINK");
    }
    
    if (pendingSystemHardwareRebootFlag && (currentMillis - hardwareRebootTimestampCount >= 2500)) {
        Serial.println("--> [WATCHDOG]: Drainage pad completed. Re-flashing core system architecture registers now and rebooting!");
        delay(10);
        NVIC_SystemReset();
    }
    
    if (currentMillis - previousTelemetryMillis >= telemetryInterval) {
        previousTelemetryMillis = currentMillis;
        
        pinMode(RADIO_SENSOR, INPUT);
        
        int rawFront = analogRead(VOLTAGE_FRONT);
        globalFrontVolts = ((rawFront * ARDUINO_REF_VOLTAGE) / 1023.0) * CALIBRATION_FRONT;
        
        int rawBack = analogRead(VOLTAGE_BACK);
        globalBackVolts = ((rawBack * ARDUINO_REF_VOLTAGE) / 1023.0) * CALIBRATION_BACK;

        frontIsCharging = (globalFrontVolts >= frontChargingVolts);
        backIsCharging = (globalBackVolts >= backChargingVolts);
        
        bool radioSenseIsActive = (digitalRead(RADIO_SENSOR) == HIGH); 
       
        if (globalFrontVolts >= 12.60) frontBatteryPercent = 100;
        else if (globalFrontVolts <= 11.50) frontBatteryPercent = 0;
        else frontBatteryPercent = (int)((globalFrontVolts - 11.50) / (12.60 - 11.50) * 100.0);
        
        if (globalBackVolts >= 12.80) backBatteryPercent = 100;
        else if (globalBackVolts <= 10.50) backBatteryPercent = 0;
        else backBatteryPercent = (int)((globalBackVolts - 10.50) / (12.80 - 10.50) * 100.0);
        
        String telemetryString = "[SYS] ";
        
        bool isWifiConnected = (WiFi.status() == WL_CONNECTED);
        if (isWifiConnected) {
            telemetryString += "IP:" + WiFi.localIP().toString() + " | "; 
        } else {
            telemetryString += "IP:STA_HOTSPOT | ";
        }

        if (lastCloudTransmitSuccessful) {
            telemetryString += "[📡 WAN_ONLINE] ";
        } else {
            telemetryString += "[☁️ WAN_OFFLINE] ";
        }
        
        if (radioSenseIsActive) { 
            telemetryString += "[🔊 AMPS ON] ";
        } else {
            telemetryString += "[🔇 AMPS OFF] ";
        }

        if (crossChargeProtectionActiveFlag) {
            telemetryString += "[⚡ CROSS_CHG ACTIVE] ";
        }
        
        if (globalFrontVolts < 6.50 && globalBackVolts < 6.50) {
            telemetryString += "BATTERIES DETECTED: [❌ BOTH DISCONNECTED]";
        }
        else {
            if (globalFrontVolts < 6.50) telemetryString += "Front: [❌ DISCONNECTED]";
            else {
                telemetryString += "Front: ";
                if (frontIsCharging) telemetryString += "[🔋 CHARGING] ";
                telemetryString += String(globalFrontVolts, 1) + "V (" + String(frontBatteryPercent) + "%)";
            }
            telemetryString += " | ";
            if (globalBackVolts < 6.50) telemetryString += "Back: [❌ DISCONNECTED]";
            else {
                telemetryString += "Back: ";
                if (backIsCharging && !frontIsCharging) telemetryString += "[🔋 CHARGING] ";
                telemetryString += String(globalBackVolts, 1) + "V (" + String(backBatteryPercent) + "%)";
            }
        }
        
        writeLog(telemetryString);

        if (!crossChargeProtectionActiveFlag) {
            if ((frontBatteryPercent <= CRITICAL_BATTERY_LOW && backBatteryPercent > 30) || 
                (backBatteryPercent <= CRITICAL_BATTERY_LOW && frontBatteryPercent > 30)) {
                
                crossChargeProtectionActiveFlag = true;
                writeLog("--> [BATTERY CRITICAL]: Threshold protection tripped! Bridging cells for emergency cross-charge.");
            }
        } 
        else {
            if ((frontBatteryPercent >= SAFE_BATTERY_CEILING && backBatteryPercent >= 30) || 
                (backBatteryPercent >= SAFE_BATTERY_CEILING && frontBatteryPercent > 30)) {
                
                crossChargeProtectionActiveFlag = false;
                writeLog("--> [BATTERY RECOVERY]: Weak bank recovered past safe 35% margin. Isolating cells.");
            }
            else if (frontBatteryPercent <= 5 && backBatteryPercent <= 5) {
                crossChargeProtectionActiveFlag = false;
                writeLog("--> [BATTERY EMERGENCY]: Both banks completely flattened! Breaking cross-charge to save core cell hardware.");
            }
        }

        if (frontIsCharging || backIsCharging || crossChargeProtectionActiveFlag) {
            if (digitalRead(RELAY_SOLENOID) == HIGH) { 
                digitalWrite(RELAY_SOLENOID, LOW);
                writeLog("--> [ISOLATOR ACTION]: Solenoid engaged. RELAY_SOLENOID CLOSED.");
            }
        } 
        else {
            if (digitalRead(RELAY_SOLENOID) == LOW) {
                digitalWrite(RELAY_SOLENOID, HIGH); // 🔴 OPEN K3 COIL CONTACTS [🗎 0.1.74]
                writeLog("--> [ISOLATOR ACTION]: Isolation active. RELAY_SOLENOID OPENED.");
            }
        }
        
        if (globalFrontVolts >= 11.20) {
            if (radioSenseIsActive) {
                if (!audioOverride && digitalRead(RELAY_AMP_REM) == HIGH) {
                    digitalWrite(RELAY_AMP_REM, LOW); 
                    writeLog("--> [AUDIO]: Radio detected active. K4 SNAP CLOSED.");
                }
            } 
            else {
                if (!audioOverride && digitalRead(RELAY_AMP_REM) == LOW) {
                    digitalWrite(RELAY_AMP_REM, HIGH); 
                    writeLog("--> [AUDIO]: Radio detected sleeping. K4 CLICK OPEN.");
                }
            }
        } 
        else {
            if (!audioOverride) {
                if (digitalRead(RELAY_AMP_REM) == LOW) {
                    digitalWrite(RELAY_AMP_REM, HIGH);
                    writeLog("--> [AUDIO]: Critical voltage protection tripped! K4 FORCED OPEN.");
                }
            }
        }

        String jsonLogArrayPayload = "[";
        int logsCompiledCount = 0;
        String temporaryNewestTimestampTrack = globalLastUploadedLogTimestamp;

        for (int i = 0; i < MAX_SYSTEM_LOGS; i++) {
            // Read backwards through your circular log history queue [🗎 0.1.290]
            int targetIndex = (currentLogWritePointerIndex - 1 - i + MAX_SYSTEM_LOGS) % MAX_SYSTEM_LOGS;
            String clearTextLine = systemLogBufferArray[targetIndex];

            if (clearTextLine.length() > 11) { // Guard gate: Line must contain a full timestamp header
                // Extract the timestamp bracket signature cleanly (e.g. "[02:58:14]")
                String lineTimestampSignature = clearTextLine.substring(0, 10);

                // 🚀 THE ABSOLUTE CLOUD FOOTPRINT CIRCUIT BREAKER:
                // If this line's timestamp signature is newer than our last uploaded anchor,
                // it is fresh content! Package it right inside your outbound telemetry payload!
                if (lineTimestampSignature > globalLastUploadedLogTimestamp) {
                    if (logsCompiledCount == 0) {
                        // Track the newest timestamp encountered in this pass to update our global anchor later
                        temporaryNewestTimestampTrack = lineTimestampSignature;
                    }

                    if (logsCompiledCount > 0) {
                        jsonLogArrayPayload += ",";
                    }
                    jsonLogArrayPayload += "\"" + clearTextLine + "\"";
                    logsCompiledCount++;
                }
            }
        }
        jsonLogArrayPayload += "]";

        globalLastUploadedLogTimestamp = temporaryNewestTimestampTrack;

        String jsonOutput = "{\"front_v\": " + String(globalFrontVolts, 2) + 
                            ",\"front_p\":" + String(frontBatteryPercent) + 
                            ",\"background_v\":" + String(globalBackVolts, 2) + 
                            ",\"back_p\":" + String(backBatteryPercent) + 
                            ",\"charging_f\":" + (frontIsCharging ? String("true") : String("false")) + 
                            ",\"charging_b\":" + (backIsCharging ? String("true") : String("false")) + 
                            ",\"cross_charging\":" + (crossChargeProtectionActiveFlag ? String("true") : String("false")) + 
                            ",\"wan_link\":" + (lastCloudTransmitSuccessful ? String("true") : String("false")) + 
                            ",\"system_logs\":" + jsonLogArrayPayload + "}";

        if (!radioSenseIsActive) {
            activeCloudPacingInterval = 30000; 
        }
        else if (currentMillis < rapidResponseWindowExpiration) {
            activeCloudPacingInterval = 2000;
        }

        if (currentMillis - lastCloudUploadTimestamp >= activeCloudPacingInterval) {
            lastCloudUploadTimestamp = currentMillis;
            transmitSecureHTTPTelemetry(jsonOutput);
        }

        longRangeAdminSyncCounter++;

        if (longRangeAdminSyncCounter >= 180) {
            writeLog("--> [WAN REFRESH]: Executing scheduled 30-minute background identity synchronization pass...");
            if (flushAdminConfigurationToCloud())
            {
                longRangeAdminSyncCounter = 0;
            }
            else
            {
                writeLog("--> [WAN REFRESH]: Failed to flush configuration to Cloudflare.");
            }
        }
    }

    while (Serial.available() > 0) {
        char c = Serial.read();
        if (c == '\n' || c == '\r') {
            incomingBuffer.trim();
            if (incomingBuffer.length() > 0) {
                processSecureCommand(incomingBuffer, "LOCAL_USB");
                incomingBuffer = "";
            }
        } else {
            incomingBuffer += c;
        }
    }
}

bool processSecureCommand(String rawPacket, String source) {
    rawPacket.trim();
    
    int colonIndex = rawPacket.indexOf(':');
    if (colonIndex == -1) {
        writeLog("--> [DENIED]: Protocol fault from " + source + " (Missing Token)");
        return false;
    }
    
    String providedToken = rawPacket.substring(0, colonIndex);
    String actionPayload = rawPacket.substring(colonIndex + 1);
    providedToken.trim();
    actionPayload.trim();
    
    unsigned int incomingHash = generateFletcher16Hash(providedToken);
    
    bool isAuthorizedLock = (incomingHash == currentLockHash);
    bool isAuthorizedUnlock = (incomingHash == currentUnlockHash);
    
    if (!isAuthorizedLock && !isAuthorizedUnlock) {
        writeLog("--> [DENIED]: Cryptographic failure (Bad Token)");
        
        if (actionPayload == "VERIFYPASS") {
            writeLog("[SYS] [❌ AUTH_FAILED]");
        }
        
        return false;
    }
    
    if (actionPayload == "REBOOT") {
        writeLog("--> [WATCHDOG]: Request to reboot. Rebooting controller now...");
        delay(1500); 
        NVIC_SystemReset();       
        return true; 
    }
    else if (actionPayload == "VERIFYPASS") {
        writeLog("--> [AUTH]: Master Passcode verified successfully over " + source + ".");
        writeLog("[SYS] [🟢 AUTH_SUCCESS]");
        return true;
    }
    else if (actionPayload == "LOCK" && isAuthorizedLock) {
        displayMatrixText(" LOCK ");
        digitalWrite(RELAY_LOCK, LOW);
        delay(400);
        digitalWrite(RELAY_LOCK, HIGH); 
        return true;
    }
    else if (actionPayload == "UNLOCK" && isAuthorizedUnlock) {
        displayMatrixText(" OPEN ");
        digitalWrite(RELAY_UNLOCK, LOW);
        delay(400);
        digitalWrite(RELAY_UNLOCK, HIGH); 
        return true;
    }
    else if (actionPayload.startsWith("SETWIFINAME=")) {
        int equalsIndex = actionPayload.indexOf('=');
        String newAP = actionPayload.substring(equalsIndex + 1); 
        newAP.trim();
        
        writeLog("--> [ADMIN]: Request to set new WIFI AP name to '" + newAP + "'.");
        
        if (newAP.length() > 2) {
            writeStringToEEPROM(EEPROM_CUSTOM_WIFI_AP, newAP);
            writeLog("--> [ADMIN_SUCCESS]: WIFI AP written. Rebooting controller...");

            flushAdminConfigurationToCloud(); 
            
            if (source == "BLE_LINK" || source == "LOCAL_USB" || source == "CLOUDFLARE_WAN_LINK") {
                writeLog("--> [WATCHDOG]: Remote origin detected. Rebooting controller now...");
                delay(1500); 
                NVIC_SystemReset();
            }
        } else {
            writeLog("--> [ADMIN_ERROR]: WIFI AP string failed length gate constraint.");
        }
        return true;
    }
    else if (actionPayload.startsWith("SETBLENAME=")) {
        int equalsIndex = actionPayload.indexOf('=');
        String newBLE = actionPayload.substring(equalsIndex + 1); 
        newBLE.trim();
        
        writeLog("--> [ADMIN]: Request to set new Bluetooth name to '" + newBLE + "'.");
        
        if (newBLE.length() > 2) {
            writeStringToEEPROM(EEPROM_CUSTOM_BLE_NAME, newBLE);
            writeLog("--> [ADMIN_SUCCESS]: BLE name written. Rebooting controller...");

            flushAdminConfigurationToCloud();
            
            if (source == "BLE_LINK" || source == "LOCAL_USB" || source == "CLOUDFLARE_WAN_LINK") {
                writeLog("--> [WATCHDOG]: Remote origin detected. Rebooting controller now...");
                delay(1500); 
                NVIC_SystemReset();
            }
        } else {
            writeLog("--> [ADMIN_ERROR]: BLE name string failed length gate constraint.");
        }
        return true;
    }
    else if (actionPayload.startsWith("SAVEROUTER=")) {
        int equalsIndex = actionPayload.indexOf('=');
        String credentialPayload = actionPayload.substring(equalsIndex + 1);
        
        int commaIndex = credentialPayload.indexOf(',');
        if (commaIndex != -1) {
            String routerSSID = credentialPayload.substring(0, commaIndex);
            String routerPASS = credentialPayload.substring(commaIndex + 1);
            routerSSID.trim(); 
            routerPASS.trim();
            
            if (routerSSID == "CLEAR") {
                writeLog("--> [ADMIN]: Request to completely purge station network profile configurations.");
                writeSecureStringToEEPROM(EEPROM_WIFI_SSID_ADDR, "");
                writeSecureStringToEEPROM(EEPROM_WIFI_PASS_ADDR, "");
                writeLog("--> [ADMIN_SUCCESS]: Router storage wiped out. Rebooting controller...");
                
                if (source == "BLE_LINK" || source == "LOCAL_USB" || source == "CLOUDFLARE_WAN_LINK") {
                    writeLog("--> [WATCHDOG]: Remote origin detected. Rebooting controller now...");
                    delay(1500); 
                    NVIC_SystemReset();
                }
                return true;
            }
            
            writeLog("--> [ADMIN]: Requesting provisional link to router: '" + routerSSID + "'...");
            
            if (routerSSID.length() > 2 && routerPASS.length() > 2) {
                WiFi.disconnect();
                delay(200);
                
                WiFi.begin(routerSSID.c_str(), routerPASS.c_str());
                
                int connectionCheckTimer = 0;
                bool connectionIsVerifiedValid = false;
                
                while (connectionCheckTimer < 12) {
                    if (WiFi.status() == WL_CONNECTED) {
                        connectionIsVerifiedValid = true;
                        break;
                    }
                    delay(500);
                    connectionCheckTimer++;
                }
                
                if (connectionIsVerifiedValid) {
                    writeLog("--> [ADMIN]: Link verified! Committing authenticated credentials to persistent memory vaults...");
                    writeLog("--> [ADMIN_SUCCESS]: Router credentials stored. Swapping networks. Rebooting controller...");
                    
                    writeSecureStringToEEPROM(EEPROM_WIFI_SSID_ADDR, routerSSID);
                    writeSecureStringToEEPROM(EEPROM_WIFI_PASS_ADDR, routerPASS);

                    flushAdminConfigurationToCloud();
                    
                    if (source == "BLE_LINK" || source == "LOCAL_USB" || source == "CLOUDFLARE_WAN_LINK") {
                        writeLog("--> [WATCHDOG]: Remote origin detected. Resetting controller now...");
                        delay(1500); 
                        NVIC_SystemReset();
                    }
                }
                else {
                    writeLog("--> [ADMIN_ERROR]: Connection probe failed! Reverting back to standalone access point.");
                    WiFi.disconnect();
                    delay(200);
                    setupWiFiAPI(); 
                    writeLog("[SYS] ROUTER_ERROR: Invalid or unreachable credentials.");
                }
            }
            else {
                writeLog("--> [ADMIN_ERROR]: Router payload failed string length constraints.");
            }
        }
        return true;
    }
    else if (actionPayload.startsWith("UPDATEMASTERPASS=")) {
        writeLog("--> [ADMIN]: Request to update master password.");
        int equalsSignIndex = actionPayload.indexOf('='); 
        String newPass = actionPayload.substring(equalsSignIndex + 1); 
        newPass.trim();
        
        if (newPass.length() > 2) {
            unsigned int newHash = generateFletcher16Hash(newPass);
            saveHashToEEPROM(EEPROM_LOCK_HASH_ADDR, newHash);
            saveHashToEEPROM(EEPROM_UNLOCK_HASH_ADDR, newHash);
            currentLockHash = newHash;
            currentUnlockHash = newHash;
            
            writeLog("--> [ADMIN]: Master Cryptographic Token Rotated.");
            delay(500); 
        } else {
            writeLog("--> [ADMIN_ERROR]: Length evaluation failed! Password string was empty.");
        }
        return true;
    }
    else if (actionPayload == "GETWIFINAME") {
        String activeAP = readStringFromEEPROM(EEPROM_CUSTOM_WIFI_AP);
        if (activeAP.length() == 0) activeAP = DEFAULT_WIFI_AP_NAME;
        writeLog("[SYS] AP_NAME:" + activeAP);
        Serial.println("--> [ADMIN]: Queried active Wi-Fi AP name.");
        return true;
    } 
    else if (actionPayload == "GETBLENAME") {
        String activeBLE = readStringFromEEPROM(EEPROM_CUSTOM_BLE_NAME);
        if (activeBLE.length() == 0) activeBLE = DEFAULT_BLE_NAME;
        writeLog("[SYS] BLE_NAME:" + activeBLE);
        Serial.println("--> [ADMIN]: Queried active Bluetooth name.");
        return true;
    } 
    else if (actionPayload == "GETROUTER") {
        String savedSSID = readSecureStringFromEEPROM(EEPROM_WIFI_SSID_ADDR);
        if (savedSSID.length() == 0) {
            writeLog("[SYS] ROUTER_SSID:[❌ NONE SAVED]");
        } else {
            writeLog("[SYS] ROUTER_SSID:" + savedSSID);
        }
        Serial.println("--> [ADMIN]: Queried link router bridge SSID properties safely.");
        return true;
    }
    else if (actionPayload == "SCANWIFI") {
        Serial.println("--> [ADMIN]: Initializing environment Wi-Fi network band scan...");
        int networksFoundCount = WiFi.scanNetworks(); 
        String payloadStringResponse = "[SYS] WIFI_LIST:";
        int accumulatedItems = 0;
        
        if (networksFoundCount == -1 || networksFoundCount == 0) {
            payloadStringResponse += "[NONE_DETECTED]";
        } else {
            for (int i = 0; i < networksFoundCount && accumulatedItems < 5; i++) {
                String currentDiscoveredSSID = WiFi.SSID(i);
                currentDiscoveredSSID.trim();
                if (currentDiscoveredSSID.length() > 0 && currentDiscoveredSSID.indexOf(',') == -1) {
                    if (accumulatedItems > 0) payloadStringResponse += ",";
                    payloadStringResponse += currentDiscoveredSSID;
                    accumulatedItems++;
                }
            }
        }
        writeLog(payloadStringResponse);
        Serial.println("--> [ADMIN_SUCCESS]: Wireless environment catalog transmitted.");
        return true;
    }
    else if (actionPayload.startsWith("SAVECFKEYS=")) {
        int equalsIndex = actionPayload.indexOf('=');
        String keyPayload = actionPayload.substring(equalsIndex + 1);
        
        int firstComma = keyPayload.indexOf(',');
        int secondComma = keyPayload.indexOf(',', firstComma + 1);
        
        if (firstComma != -1 && secondComma != -1) {
            String routerCfHost   = keyPayload.substring(0, firstComma);
            String routerCfId     = keyPayload.substring(firstComma + 1, secondComma);
            String routerCfSecret = keyPayload.substring(secondComma + 1);
            
            routerCfHost.trim();
            routerCfId.trim();
            routerCfSecret.trim();

            writeLog("--> [ADMIN]: Processing consolidated Cloudflare credentials flash block...");
            
            if (routerCfHost.length() > 5 && routerCfId.length() > 10 && routerCfSecret.length() > 10) {
                writeStringToEEPROM(EEPROM_CF_HOST_ADDR, routerCfHost);
                writeSecureStringToEEPROM(EEPROM_CF_CLIENT_ID_ADDR, routerCfId);
                writeSecureStringToEEPROM(EEPROM_CF_SECRET_ADDR, routerCfSecret);
                
                writeLog("--> [ADMIN_SUCCESS]: Complete Zero-Trust profile committed to storage vaults! Rebooting controller...");
                
                delay(1500); 
                NVIC_SystemReset();
                return true;
            } else {
                writeLog("--> [ADMIN_ERROR]: Payload segments failed baseline length verification constraints.");
            }
        } else {
            writeLog("--> [ADMIN_ERROR]: Corrupted credentials envelope configuration shape. Missing commas.");
        }
        return true;
    }
    else if (actionPayload == "GETCFKEYS") {
        Serial.println("--> [ADMIN]: App requested secure remote configuration sync pass...");
        
        String activeCfHost   = readStringFromEEPROM(EEPROM_CF_HOST_ADDR);
        String activeCfId     = readSecureStringFromEEPROM(EEPROM_CF_CLIENT_ID_ADDR);
        String activeCfSecret = readSecureStringFromEEPROM(EEPROM_CF_SECRET_ADDR);
        
        if (activeCfId.length() > 0 && activeCfSecret.length() > 0) {
            String rawKeysBuffer = activeCfHost + "," + activeCfId + "," + activeCfSecret;
            
            String encryptedKeysBase64 = encryptPayloadAES128CBC(rawKeysBuffer);
            
            String cfKeysPayloadResponse = "[SYS] CF_KEYS:" + encryptedKeysBase64;
            writeLog(cfKeysPayloadResponse);
            Serial.println("--> [ADMIN_SUCCESS]: Stored Zero-Trust key vectors encrypted and off-loaded safely.");
        } 
        else {
            writeLog("[SYS] CF_KEYS:ERR_EMPTY_VAULTS");
            Serial.println("--> [ADMIN_WARN]: Key retrieval aborted. Stored vaults are currently unconfigured.");
        }
        return true;
    }

    return false;
}

void setupWiFiAPI() {
    String savedSSID = readSecureStringFromEEPROM(EEPROM_WIFI_SSID_ADDR);
    String savedPASS = readSecureStringFromEEPROM(EEPROM_WIFI_PASS_ADDR);
    
    if (savedSSID.length() > 0) {
        WiFi.begin(savedSSID.c_str(), savedPASS.c_str());
        int attempts = 0;
        while (WiFi.status() != WL_CONNECTED && attempts < 10) {
            delay(500); attempts++;
        }
    }
    
    if (WiFi.status() != WL_CONNECTED) {
        WiFi.beginAP(currentBroadcastAP.c_str(), "VersaCore99");
    } else {
        RTC.begin();
        unsigned long globalEpochTime = WiFi.getTime();
        if (globalEpochTime > 0) {
            RTCTime activeTimeConvert(globalEpochTime);
            RTC.setTime(activeTimeConvert);
        }
    }

    webServer.begin();
}

void handleWiFiAPI() {
    WiFiClient client = webServer.available();
    if (!client) return;
    
    String requestBuffer = "";
    unsigned long timeout = millis();
    
    while (client.connected() && millis() - timeout < 1000) {
        if (client.available()) {
            char c = client.read();
            requestBuffer += c;
            if (requestBuffer.endsWith("\r\n\r\n")) {
                String body = "";
                while (client.available()) { body += (char)client.read(); }
                body.trim();
                
                if (requestBuffer.indexOf("POST /api/telemetry") != -1) {
                    if (body.length() > 0) {
                        String decryptedTelemetryPasscode = decryptPayloadAES128CBC(body);
                        decryptedTelemetryPasscode.trim();
                        
                        int absolutePassLength = decryptedTelemetryPasscode.length();
                        while (absolutePassLength > 0 && (decryptedTelemetryPasscode[absolutePassLength - 1] < 32 || decryptedTelemetryPasscode[absolutePassLength - 1] > 126)) {
                            decryptedTelemetryPasscode.remove(absolutePassLength - 1);
                            absolutePassLength = decryptedTelemetryPasscode.length();
                        }
                        
                        unsigned int computedIncomingHash = generateFletcher16Hash(decryptedTelemetryPasscode);
                        
                        if (computedIncomingHash == currentLockHash || computedIncomingHash == currentUnlockHash) {
                            String jsonLogArrayPayload = "[";
                            int logsCompiledCount = 0;
                            for (int i = 0; i < MAX_SYSTEM_LOGS; i++) {
                                int targetEvaluationIndex = (currentLogWritePointerIndex - 1 - i + MAX_SYSTEM_LOGS) % MAX_SYSTEM_LOGS;
                                String individualLogLine = systemLogBufferArray[targetEvaluationIndex];
                                if (individualLogLine.length() > 0) {
                                    if (logsCompiledCount > 0) jsonLogArrayPayload += ",";
                                    jsonLogArrayPayload += "\"" + individualLogLine + "\"";
                                    logsCompiledCount++;
                                }
                            }
                            jsonLogArrayPayload += "]";
                            
                            for (int i = 0; i < MAX_SYSTEM_LOGS; i++) { systemLogBufferArray[i] = ""; }
                            currentLogWritePointerIndex = 0;
                            
                            String json = "{\"front_v\":" + String(globalFrontVolts, 2) + 
                                          ",\"front_p\":" + String(frontBatteryPercent) + 
                                          ",\"background_v\":" + String(globalBackVolts, 2) + 
                                          ",\"back_p\":" + String(backBatteryPercent) + 
                                          ",\"charging_f\":" + String(frontIsCharging ? "true" : "false") + 
                                          ",\"charging_b\":" + String(backIsCharging ? "true" : "false") + 
                                          ",\"cross_charging\":" + (crossChargeProtectionActiveFlag ? String("true") : String("false")) + 
                                          ",\"wan_link\":" + String(lastCloudTransmitSuccessful ? "true" : "false") + 
                                          ",\"system_logs\":" + jsonLogArrayPayload + "}";
                            
                            client.println("HTTP/1.1 200 OK");
                            client.println("Content-Type: application/json");
                            client.println("Connection: close");
                            client.println();
                            client.print(json);
                        } else {
                            writeLog("--> [SECURITY WARN]: Telemetry Hash mismatch! ReJECTING Wi-Fi request.");
                            client.println("HTTP/1.1 401 Unauthorized");
                            client.println("Connection: close");
                            client.println();
                        }
                    } else {
                        client.println("HTTP/1.1 400 Bad Request");
                        client.println("Connection: close");
                        client.println();
                    }
                    break;
                }
                else if (requestBuffer.indexOf("POST /api/command") != -1) {
                    if (body.length() > 0) {
                        writeLog("--> [NET REST CRYPTO]: Decoding command payload via embedded cipher grid...");
                        String fullyDecryptedBodyString = decryptPayloadAES128CBC(body);
                        fullyDecryptedBodyString.trim();
                        
                        if (fullyDecryptedBodyString.length() > 0 && fullyDecryptedBodyString.indexOf(':') != -1) {
                            if (fullyDecryptedBodyString.indexOf("SETBLENAME=") != -1 || fullyDecryptedBodyString.indexOf("SETWIFINAME=") != -1) {
                                processSecureCommand(fullyDecryptedBodyString, "WIFI_API_ADMIN");
                                client.println("HTTP/1.1 200 OK");
                                client.println("Content-Type: application/json");
                                client.println("Connection: close");
                                client.println();
                                client.print("{\"status\":\"Success\"}");
                                
                                pendingSystemHardwareRebootFlag = true;
                                hardwareRebootTimestampCount = millis();
                                writeLog("--> [NET REST API]: Admin change committed. Success response sent. Queueing delayed reboot...");
                            }
                            else if (processSecureCommand(fullyDecryptedBodyString, "WIFI_API")) {
                                client.println("HTTP/1.1 200 OK");
                                client.println("Content-Type: application/json");
                                client.println("Connection: close");
                                client.println();
                                client.print("{\"status\":\"Success\"}");
                            } else {
                                client.println("HTTP/1.1 401 Unauthorized");
                                client.println("Connection: close");
                                client.println();
                                client.print("{\"status\":\"Denied\"}");
                            }
                        } else {
                            client.println("HTTP/1.1 400 Bad Request");
                            client.println("Connection: close");
                            client.println();
                        }
                    } else {
                        client.println("HTTP/1.1 400 Bad Request");
                        client.println("Connection: close");
                        client.println();
                    }
                    break;
                }
                else if (requestBuffer.indexOf("GET /api/status") != -1) {
                    Serial.println("--> [NET REST STATUS]: Discovery probe received. Responding Ready.");
                    client.println("HTTP/1.1 200 OK");
                    client.println("Content-Type: application/json");
                    client.println("Connection: close");
                    client.println();
                    client.print("{\"status\":\"Ready\"}"); 
                    break;
                }
                else if (requestBuffer.indexOf("GET /api/admin") != -1) {
                    Serial.println("--> [NET REST API]: Generating clean administration profile payload...");
                    
                    String activeAP = readStringFromEEPROM(EEPROM_CUSTOM_WIFI_AP);
                    if (activeAP.length() == 0) activeAP = DEFAULT_WIFI_AP_NAME;
                    
                    String activeBLE = readStringFromEEPROM(EEPROM_CUSTOM_BLE_NAME);
                    if (activeBLE.length() == 0) activeBLE = DEFAULT_BLE_NAME;
                    
                    String savedSSID = readSecureStringFromEEPROM(EEPROM_WIFI_SSID_ADDR);
                    if (savedSSID.length() == 0) savedSSID = "NONE";

                    String savedCfHost = readStringFromEEPROM(EEPROM_CF_HOST_ADDR);
                    if (savedCfHost.length() == 0) savedCfHost = "silent-bird-d9c0.taigon1984.workers.dev";

                    String savedCfId = readSecureStringFromEEPROM(EEPROM_CF_CLIENT_ID_ADDR);
                    if (savedCfId.length() == 0) savedCfId = "NONE";
                    
                    String jsonAdminProfile = "{\"wifi_ap\":\"" + activeAP + "\"" +
                                              ",\"ble_name\":\"" + activeBLE + "\"" +
                                              ",\"router_ssid\":\"" + savedSSID + "\"" +
                                              ",\"cf_host\":\"" + savedCfHost + "\"" +
                                              ",\"cf_id\":\"" + savedCfId + "\"}";

                    String encryptedPayload = encryptPayloadAES128CBC(jsonAdminProfile);
                                              
                    client.println("HTTP/1.1 200 OK");
                    client.println("Content-Type: application/json");
                    client.println("Connection: close");
                    client.println();
                    client.print(encryptedPayload);

                    Serial.println("--> [ADMIN REST SUCCESS]: Transmitted encrypted administrative payload profile string.");
                    break;
                }
            }
        }
    }
    delay(1);
    if (client) {
        client.flush(); 
        delay(15); 
        client.stop(); 
    }
}

void setupBluetoothNetwork() {
    if (!BLE.begin()) return;
    BLE.setLocalName(currentBroadcastBLE.c_str());
    BLE.setAdvertisedService(hubService);
    hubService.addCharacteristic(rxCharacteristic);
    hubService.addCharacteristic(txCharacteristic);
    BLE.addService(hubService);
    BLE.advertise();
}

unsigned int generateFletcher16Hash(String data) {
    unsigned int sum1 = 0; unsigned int sum2 = 0;
    for (int i = 0; i < data.length(); i++) {
        sum1 = (sum1 + data[i]) % 255; sum2 = (sum2 + sum1) % 255;
    }
    return (sum2 << 8) | sum1;
}

void saveHashToEEPROM(int addr, unsigned int hash) {
    EEPROM.write(addr, (hash & 0xFF)); EEPROM.write(addr + 1, ((hash >> 8) & 0xFF));
}

unsigned int readHashFromEEPROM(int addr) {
    return ((EEPROM.read(addr + 1) << 8) | EEPROM.read(addr));
}

void writeSecureStringToEEPROM(int addr, String data) {
    int len = data.length(); 
    EEPROM.write(addr, len);
    for (int i = 0; i < len; i++) { 
        uint8_t secureByte = (uint8_t)data[i] ^ CRYPTO_SALT_KEY[i % 16];
        EEPROM.write(addr + 1 + i, secureByte); 
    }
}

String readSecureStringFromEEPROM(int addr) {
    int len = EEPROM.read(addr); 
    
    if (len == 255 || len == 0 || len > 128) return "";
    
    String res = ""; 
    for (int i = 0; i < len; i++) { 
        uint8_t normalByte = EEPROM.read(addr + 1 + i) ^ CRYPTO_SALT_KEY[i % 16];
        res += (char)normalByte; 
    }
    return res;
}

void writeStringToEEPROM(int addr, String data) {
    int len = data.length(); EEPROM.write(addr, len);
    for (int i = 0; i < len; i++) { EEPROM.write(addr + 1 + i, data[i]); }
}

String readStringFromEEPROM(int addr) {
    int len = EEPROM.read(addr); if (len == 255 || len == 0) return "";
    String res = ""; for (int i = 0; i < len; i++) { res += (char)EEPROM.read(addr + 1 + i); }
    return res;
}

void displayMatrixText(String txt) {
    activeDashboardText = txt;
    writeLog("[DASHBOARD ALERT]: " + txt);
}

// ====================================================================
// EMBEDDED AES-128-CBC & BASE64 DECRYPTION ENGINE 🔒
// ====================================================================
static const uint8_t sbox[256] = {
    0x63, 0x7c, 0x77, 0x7b, 0xf2, 0x6b, 0x6f, 0xc5, 0x30, 0x01, 0x67, 0x2b, 0xfe, 0xd7, 0xab, 0x76,
    0xca, 0x82, 0xc9, 0x7d, 0xfa, 0x59, 0x47, 0xf0, 0xad, 0xd4, 0xa2, 0xaf, 0x9c, 0xa4, 0x72, 0xc0,
    0xb7, 0xfd, 0x93, 0x26, 0x36, 0x3f, 0xf7, 0xcc, 0x34, 0xa5, 0xe5, 0xf1, 0x71, 0xd8, 0x31, 0x15,
    0x04, 0xc7, 0x23, 0xc3, 0x18, 0x96, 0x05, 0x9a, 0x07, 0x12, 0x80, 0xe2, 0xeb, 0x27, 0xb2, 0x75,
    0x09, 0x83, 0x2c, 0x1a, 0x1b, 0x6e, 0x5a, 0xa0, 0x52, 0x3b, 0xd6, 0xb3, 0x29, 0xe3, 0x2f, 0x84,
    0x53, 0xd1, 0x00, 0xed, 0x20, 0xfc, 0xb1, 0x5b, 0x6a, 0xcb, 0xbe, 0x39, 0x4a, 0x4c, 0x58, 0xcf,
    0xd0, 0xef, 0xaa, 0xfb, 0x43, 0x4d, 0x33, 0x85, 0x45, 0xf9, 0x02, 0x7f, 0x50, 0x3c, 0x9f, 0xa8,
    0x51, 0xa3, 0x40, 0x8f, 0x92, 0x9d, 0x38, 0xf5, 0xbc, 0xb6, 0xda, 0x21, 0x10, 0xff, 0xf3, 0xd2,
    0xcd, 0x0c, 0x13, 0xec, 0x5f, 0x97, 0x44, 0x17, 0xc4, 0xa7, 0x7e, 0x3d, 0x64, 0x5d, 0x19, 0x73,
    0x60, 0x81, 0x4f, 0xdc, 0x22, 0x2a, 0x90, 0x88, 0x46, 0xee, 0xb8, 0x14, 0xde, 0x5e, 0x0b, 0xdb,
    0xe0, 0x32, 0x3a, 0x0a, 0x49, 0x06, 0x24, 0x5c, 0xc2, 0xd3, 0xac, 0x62, 0x91, 0x95, 0xe4, 0x79,
    0xe7, 0xc8, 0x37, 0x6d, 0x8d, 0xd5, 0x4e, 0xa9, 0x6c, 0x56, 0xf4, 0xea, 0x65, 0x7a, 0xae, 0x08,
    0xba, 0x78, 0x25, 0x2e, 0x1c, 0xa6, 0xb4, 0xc6, 0xe8, 0xdd, 0x74, 0x1f, 0x4b, 0xbd, 0x8b, 0x8a,
    0x70, 0x3e, 0xb5, 0x66, 0x48, 0x03, 0xf6, 0x0e, 0x61, 0x35, 0x57, 0xb9, 0x86, 0xc1, 0x1d, 0x9e,
    0xe1, 0xf8, 0x98, 0x11, 0x69, 0xd9, 0x8e, 0x94, 0x9b, 0x1e, 0x87, 0xe9, 0xce, 0x55, 0x28, 0xdf,
    0x8c, 0xa1, 0x89, 0x0d, 0xbf, 0xe6, 0x42, 0x68, 0x41, 0x99, 0x2d, 0x0f, 0xb0, 0x54, 0xbb, 0x16
};

static const uint8_t rsbox[256] = {
    0x52, 0x09, 0x6a, 0xd5, 0x30, 0x36, 0xa5, 0x38, 0xbf, 0x40, 0xa3, 0x9e, 0x81, 0xf3, 0xd7, 0xfb,
    0x7c, 0xe3, 0x39, 0x82, 0x9b, 0x2f, 0xff, 0x87, 0x34, 0x8e, 0x43, 0x44, 0xc4, 0xde, 0xe9, 0xcb,
    0x54, 0x7b, 0x94, 0x32, 0xa6, 0xc2, 0x23, 0x3d, 0xee, 0x4c, 0x95, 0x0b, 0x42, 0xfa, 0xc3, 0x4e,
    0x08, 0x2e, 0xa1, 0x66, 0x28, 0xd9, 0x24, 0xb2, 0x76, 0x5b, 0xa2, 0x49, 0x6d, 0x8b, 0xd1, 0x25,
    0x72, 0xf8, 0xf6, 0x64, 0x86, 0x68, 0x98, 0x16, 0xd4, 0xa4, 0x5c, 0xcc, 0x5d, 0x65, 0xb6, 0x92,
    0x6c, 0x70, 0x48, 0x50, 0xfd, 0xed, 0xb9, 0xda, 0x5e, 0x15, 0x46, 0x57, 0xa7, 0x8d, 0x9d, 0x84,
    0x90, 0xd8, 0xab, 0x00, 0x8c, 0xbc, 0xd3, 0x0a, 0xf7, 0xe4, 0x58, 0x05, 0xb8, 0xb3, 0x45, 0x06,
    0xd0, 0x2c, 0x1e, 0x8f, 0xca, 0x3f, 0x0f, 0x02, 0xc1, 0xaf, 0xbd, 0x03, 0x01, 0x13, 0x8a, 0x6b,
    0x3a, 0x91, 0x11, 0x41, 0x4f, 0x67, 0xdc, 0xea, 0x97, 0xf2, 0xcf, 0xce, 0xf0, 0xb4, 0xe6, 0x73,
    0x96, 0xac, 0x74, 0x22, 0xe7, 0xad, 0x35, 0x85, 0xe2, 0xf9, 0x37, 0xe8, 0x1c, 0x75, 0xdf, 0x6e,
    0x47, 0xf1, 0x1a, 0x71, 0x1d, 0x29, 0xc5, 0x89, 0x6f, 0xb7, 0x62, 0x0e, 0xaa, 0x18, 0xbe, 0x1b,
    0xfc, 0x56, 0x3e, 0x4b, 0xc6, 0xd2, 0x79, 0x20, 0x9a, 0xdb, 0xc0, 0xfe, 0x78, 0xcd, 0x5a, 0xf4,
    0x1f, 0xdd, 0xa8, 0x33, 0x88, 0x07, 0xc7, 0x31, 0xb1, 0x12, 0x10, 0x59, 0x27, 0x80, 0xec, 0x5f,
    0x60, 0x51, 0x7f, 0xa9, 0x19, 0xb5, 0x4a, 0x0d, 0x2d, 0xe5, 0x7a, 0x9f, 0x93, 0xc9, 0x9c, 0xef,
    0xa0, 0xe0, 0x3b, 0x4d, 0xae, 0x2a, 0xf5, 0xb0, 0xc8, 0xeb, 0xbb, 0x3c, 0x83, 0x53, 0x99, 0x61,
    0x17, 0x2b, 0x04, 0x7e, 0xba, 0x77, 0xd6, 0x26, 0xe1, 0x69, 0x14, 0x63, 0x55, 0x21, 0x0c, 0x7d
};

void aes_InvCipher(uint8_t* state, const uint8_t* roundKeys) {
    auto AddRoundKey = [](uint8_t* st, const uint8_t* key) {
        for (int i = 0; i < 16; ++i) st[i] ^= key[i];
    };
    auto InvSubBytes = [](uint8_t* st) {
        for (int i = 0; i < 16; ++i) st[i] = rsbox[st[i]];
    };
    auto InvShiftRows = [](uint8_t* st) {
        uint8_t tmp;
        tmp = st[13]; st[13] = st[9]; st[9] = st[5]; st[5] = st[1]; st[1] = tmp;
        tmp = st[2]; st[2] = st[10]; st[10] = tmp; tmp = st[6]; st[6] = st[14]; st[14] = tmp;
        tmp = st[3]; st[3] = st[7]; st[7] = st[11]; st[11] = st[15]; st[15] = tmp;
    };
    auto InvMixColumns = [](uint8_t* st) {
        auto g2 = [](uint8_t x) { return (x << 1) ^ ((x & 0x80) ? 0x1b : 0x00); };
        auto g4 = [&](uint8_t x) { return g2(g2(x)); };
        auto g8 = [&](uint8_t x) { return g2(g4(x)); };
        auto g9 = [&](uint8_t x) { return g8(x) ^ x; };
        auto g11 = [&](uint8_t x) { return g8(x) ^ g2(x) ^ x; };
        auto g13 = [&](uint8_t x) { return g8(x) ^ g4(x) ^ x; };
        auto g14 = [&](uint8_t x) { return g8(x) ^ g4(x) ^ g2(x); };
        for (int i = 0; i < 4; ++i) {
            uint8_t c0 = st[i*4], c1 = st[i*4+1], c2 = st[i*4+2], c3 = st[i*4+3];
            st[i*4] = g14(c0) ^ g11(c1) ^ g13(c2) ^ g9(c3);
            st[i*4+1] = g9(c0) ^ g14(c1) ^ g11(c2) ^ g13(c3);
            st[i*4+2] = g13(c0) ^ g9(c1) ^ g14(c2) ^ g11(c3);
            st[i*4+3] = g11(c0) ^ g13(c1) ^ g9(c2) ^ g14(c3);
        }
    };
    
    AddRoundKey(state, roundKeys + 160);
    for (int round = 9; round >= 1; --round) {
        InvShiftRows(state);
        InvSubBytes(state);
        AddRoundKey(state, roundKeys + round * 16);
        InvMixColumns(state);
    }
    InvShiftRows(state);
    InvSubBytes(state);
    AddRoundKey(state, roundKeys);
}

void aes_KeyExpansion(const uint8_t* key, uint8_t* roundKeys) {
    const uint32_t rcon[11] = {0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1b, 0x36};
    for (int i = 0; i < 16; ++i) roundKeys[i] = key[i];
    for (int i = 16; i < 176; i += 4) {
        uint8_t tmp[4] = {roundKeys[i-4], roundKeys[i-3], roundKeys[i-2], roundKeys[i-1]};
        if (i % 16 == 0) {
            uint8_t k = tmp[0]; tmp[0] = tmp[1]; tmp[1] = tmp[2]; tmp[2] = tmp[3]; tmp[3] = k;
            for (int j = 0; j < 4; ++j) tmp[j] = sbox[tmp[j]];
            tmp[0] ^= rcon[i / 16];
        }
        for (int j = 0; j < 4; ++j) roundKeys[i+j] = roundKeys[i-16+j] ^ tmp[j];
    }
}

String decryptPayloadAES128CBC(const String& base64Input) {
    auto b64_dec = [](char c) -> int {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '+') return 62; if (c == '/') return 63; return -1;
    };
    
    // Dynamically strip out any illegal trailing carriage returns, newlines, spaces, 
    // or corrupted control characters BEFORE running any array parsing math loops!
    String sanitizedBase64 = "";
    for (int i = 0; i < base64Input.length(); i++) {
        char c = base64Input[i];
        // Only accept valid Base64 characters, plus the padding character '='
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=') {
            sanitizedBase64 += c;
        }
    }
    
    int l = sanitizedBase64.length();
    if (l == 0 || l % 4 != 0) return ""; // Reject badly formatted blocks cleanly
    
    int padding = (sanitizedBase64[l-1] == '=') + (sanitizedBase64[l-2] == '=');
    size_t cipherLen = (l * 3) / 4 - padding;
    uint8_t* cipherBytes = (uint8_t*)malloc(cipherLen);
    if (!cipherBytes) return "";
    
    int j = 0;
    for (int i = 0; i < l; i += 4) {
        int c1 = b64_dec(sanitizedBase64[i]);
        int c2 = b64_dec(sanitizedBase64[i+1]);
        int c3 = (sanitizedBase64[i+2] == '=') ? 0 : b64_dec(sanitizedBase64[i+2]);
        int c4 = (sanitizedBase64[i+3] == '=') ? 0 : b64_dec(sanitizedBase64[i+3]);
        
        if (c1 == -1 || c2 == -1 || c3 == -1 || c4 == -1) {
            free(cipherBytes);
            return "";
        }
        
        int n = (c1 << 18) | (c2 << 12) | (c3 << 6) | c4;
        if (j < cipherLen) cipherBytes[j++] = (n >> 16) & 0xFF;
        if (j < cipherLen) cipherBytes[j++] = (n >> 8) & 0xFF;
        if (j < cipherLen) cipherBytes[j++] = n & 0xFF;
    }
    
    if (cipherLen == 0 || cipherLen % 16 != 0) { free(cipherBytes); return ""; }
    
    uint8_t roundKeys[176];
    aes_KeyExpansion(aesSecretKeyBytes, roundKeys);
    
    uint8_t prev_block[16];
    memcpy(prev_block, initializationVector, 16);
    
    uint8_t* plainBytes = (uint8_t*)malloc(cipherLen + 1);
    if (!plainBytes) { free(cipherBytes); return ""; }
    
    for (size_t block = 0; block < cipherLen; block += 16) {
        uint8_t current_block[16];
        memcpy(current_block, cipherBytes + block, 16);
        aes_InvCipher(cipherBytes + block, roundKeys);
        for (int i = 0; i < 16; ++i) {
            plainBytes[block + i] = (cipherBytes + block)[i] ^ prev_block[i];
        }
        memcpy(prev_block, current_block, 16);
    }
    
    free(cipherBytes);
    
    uint8_t padVal = plainBytes[cipherLen - 1];
    size_t plainLen = cipherLen;
    if (padVal >= 1 && padVal <= 16) {
        plainLen = cipherLen - padVal;
    }
    
    String outStr = "";
    for (size_t i = 0; i < plainLen; i++) {
        if (plainBytes[i] >= 32 && plainBytes[i] <= 126) {
            outStr += (char)plainBytes[i];
        }
    }
    
    free(plainBytes); 
    return outStr;
}

void aes_Cipher(uint8_t* state, const uint8_t* roundKeys) {
    auto AddRoundKey = [](uint8_t* st, const uint8_t* key) {
        for (int i = 0; i < 16; ++i) st[i] ^= key[i];
    };
    auto SubBytes = [](uint8_t* st) {
        for (int i = 0; i < 16; ++i) st[i] = sbox[st[i]];
    };
    auto ShiftRows = [](uint8_t* st) {
        uint8_t tmp;
        tmp = st[1]; st[1] = st[5]; st[5] = st[9]; st[9] = st[13]; st[13] = tmp;
        tmp = st[2]; st[2] = st[10]; st[10] = tmp; tmp = st[6]; st[6] = st[14]; st[14] = tmp;
        tmp = st[15]; st[15] = st[11]; st[11] = st[7]; st[7] = st[3]; st[3] = tmp;
    };
    auto MixColumns = [](uint8_t* st) {
        auto g2 = [](uint8_t x) { return (x << 1) ^ ((x & 0x80) ? 0x1b : 0x00); };
        for (int i = 0; i < 4; ++i) {
            uint8_t c0 = st[i*4], c1 = st[i*4+1], c2 = st[i*4+2], c3 = st[i*4+3];
            st[i*4]   = g2(c0) ^ (g2(c1) ^ c1) ^ c2 ^ c3;
            st[i*4+1] = c0 ^ g2(c1) ^ (g2(c2) ^ c2) ^ c3;
            st[i*4+2] = c0 ^ c1 ^ g2(c2) ^ (g2(c3) ^ c3);
            st[i*4+3] = (g2(c0) ^ c0) ^ c1 ^ c2 ^ g2(c3);
        }
    };

    AddRoundKey(state, roundKeys);
    for (int round = 1; round <= 9; ++round) {
        SubBytes(state);
        ShiftRows(state);
        MixColumns(state);
        AddRoundKey(state, roundKeys + round * 16);
    }
    SubBytes(state);
    ShiftRows(state);
    AddRoundKey(state, roundKeys + 160);
}

String encryptPayloadAES128CBC(const String& plainInput) {
    auto b64_enc = [](int val) -> char {
        if (val >= 0 && val <= 255) {
            const char b64Chars[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            return b64Chars[val];
        }
        return '=';
    };

    size_t plainLen = plainInput.length();
    uint8_t padVal = 16 - (plainLen % 16);
    size_t cipherLen = plainLen + padVal;

    uint8_t* cipherBytes = (uint8_t*)malloc(cipherLen);
    if (!cipherBytes) return "";

    // Load plaintext and apply standard PKCS7 padding
    memcpy(cipherBytes, plainInput.c_str(), plainLen);
    memset(cipherBytes + plainLen, padVal, padVal);

    uint8_t roundKeys[176];
    aes_KeyExpansion(aesSecretKeyBytes, roundKeys);

    uint8_t prev_block[16];
    memcpy(prev_block, initializationVector, 16);

    for (size_t block = 0; block < cipherLen; block += 16) {
        for (int i = 0; i < 16; ++i) {
            cipherBytes[block + i] ^= prev_block[i];
        }
        aes_Cipher(cipherBytes + block, roundKeys);
        memcpy(prev_block, cipherBytes + block, 16);
    }

    // Convert fully encrypted bytes directly into an output Base64 string block
    String base64Output = "";
    for (size_t i = 0; i < cipherLen; i += 3) {
        uint32_t n = (cipherBytes[i] << 16) | 
                     ((i + 1 < cipherLen ? cipherBytes[i+1] : 0) << 8) | 
                      (i + 2 < cipherLen ? cipherBytes[i+2] : 0);

        base64Output += b64_enc((n >> 18) & 63);
        base64Output += b64_enc((n >> 12) & 63);
        base64Output += (i + 1 < cipherLen) ? b64_enc((n >> 6) & 63) : '=';
        base64Output += (i + 2 < cipherLen) ? b64_enc(n & 63) : '=';
    }

    free(cipherBytes);
    return base64Output;
}

void transmitSecureHTTPTelemetry(String jsonPayload) {
    bool isWifiConnected = (WiFi.status() == WL_CONNECTED);
    if (!isWifiConnected) return;

    bool hasValidCredentials = CLOUDFLARE_HOST.length() > 5 && 
                               !CLOUDFLARE_HOST.equals("silent-bird-d9c0.taigon1984.workers.dev") &&
                               CF_CLIENT_ID.length() > 5 && 
                               CF_CLIENT_SECRET.length() > 5;

    if (!hasValidCredentials) return;

    WiFiSSLClient secureClient;

    Serial.println("--> [WAN HTTPS]: Opening hardware-accelerated TLS 443 channel to Cloudflare edge...");

    if (secureClient.connect(CLOUDFLARE_HOST.c_str(), 443)) { 
        Serial.println("--> [WAN HTTPS SUCCESS]: Handshake authorized! Flushing data payload..."); 

        secureClient.println("POST /api/telemetry HTTP/1.1");
        secureClient.println("Host: " + CLOUDFLARE_HOST);
        secureClient.println("Content-Type: text/plain");
        secureClient.println("CF-Access-Client-Id: " + CF_CLIENT_ID);
        secureClient.println("CF-Access-Client-Secret: " + CF_CLIENT_SECRET);
        secureClient.println("Content-Length: " + String(jsonPayload.length()));
        secureClient.println("Connection: close");
        secureClient.println();
        secureClient.print(jsonPayload);

        while (secureClient.connected()) {
            String responseLine = secureClient.readStringUntil('\n');
            responseLine.trim();
            
            if (responseLine.length() == 0) {
                break;
            }
        }

        String inboundWanCommandBody = "";
        while (secureClient.available()) {
            char incomingByteChar = secureClient.read();
            inboundWanCommandBody += incomingByteChar;
        }
        inboundWanCommandBody.trim();

        secureClient.stop();
        lastCloudTransmitSuccessful = true;

        if (inboundWanCommandBody.length() > 0 && inboundWanCommandBody != "NONE") {
            if (inboundWanCommandBody.indexOf("Server:") != -1 || inboundWanCommandBody.indexOf("CF-RAY") != -1) {
                return;
            }

            writeLog("--> [WAN OVER-THE-AIR COMMAND]: Intercepted active remote payload envelope!");
            writeLog("--> [WAN COMMAND PAYLOAD]: " + inboundWanCommandBody);

            String fullyDecryptedBodyString = decryptPayloadAES128CBC(inboundWanCommandBody);
            fullyDecryptedBodyString.trim();
            
            if (fullyDecryptedBodyString.length() > 0 && fullyDecryptedBodyString.indexOf(':') != -1) {
                rapidResponseWindowExpiration = millis() + 20000;

                if (fullyDecryptedBodyString.indexOf("SETBLENAME=") != -1 || 
                    fullyDecryptedBodyString.indexOf("SETWIFINAME=") != -1 ||
                    fullyDecryptedBodyString.indexOf("SAVEROUTER=") != -1) { 
                    
                    processSecureCommand(fullyDecryptedBodyString, "WIFI_API_ADMIN"); 
                    
                    pendingSystemHardwareRebootFlag = true; 
                    hardwareRebootTimestampCount = millis(); 
                    writeLog("--> [NET REST API WAN]: Admin profile updated over cellular data. Queueing automated safety reboot...");
                }
                else if (processSecureCommand(fullyDecryptedBodyString, "CLOUDFLARE_WAN_LINK")) { 
                    writeLog("--> [NET COMMAND SUCCESS WAN]: Rapid over-the-air remote action executed cleanly.");
                } 
                else {
                    writeLog("--> [DENIED WAN]: Cryptographic validation rejected token.");
                }
            }
            else {
                writeLog("--> [WAN DECRYPT ERROR]: Cipher compilation structural fault or bad key framing layout.");
            }
        }
    } 
    else {
        writeLog("--> [WAN HTTPS ERROR]: Handshake aborted. Edge network unreachable.");
        lastCloudTransmitSuccessful = false;
    }
}

bool flushAdminConfigurationToCloud() {
    if (WiFi.status() != WL_CONNECTED) {
        return false; 
    }

    String activeAP = readStringFromEEPROM(EEPROM_CUSTOM_WIFI_AP);
    if (activeAP.length() == 0) activeAP = DEFAULT_WIFI_AP_NAME;

    String activeBLE = readStringFromEEPROM(EEPROM_CUSTOM_BLE_NAME);
    if (activeBLE.length() == 0) activeBLE = DEFAULT_BLE_NAME;

    String savedSSID = readSecureStringFromEEPROM(EEPROM_WIFI_SSID_ADDR);
    if (savedSSID.length() == 0) {
        savedSSID = "NONE";
    }

    String configJsonPayload = "{\"wifi_ap\":\"" + activeAP + "\"" +
                               ",\"ble_name\":\"" + activeBLE + "\"" +
                               ",\"router_ssid\":\"" + savedSSID + "\"}";

    if (lastAdminPayload == configJsonPayload) {
        return true; 
    }

    Serial.println("Payload to be transmitted to Cloudflare: " + configJsonPayload);

    bool hasValidCredentials = CLOUDFLARE_HOST.length() > 5 && 
                               CF_CLIENT_ID.length() > 5 && 
                               CF_CLIENT_SECRET.length() > 5;

    if (!hasValidCredentials) return false;

    WiFiSSLClient secureClient;
    Serial.println("--> [WAN HTTPS CONFIG]: Offloading identities to persistent KV vaults...");

    if (secureClient.connect(CLOUDFLARE_HOST.c_str(), 443)) {
        secureClient.println("POST /api/admin HTTP/1.1");
        secureClient.println("Host: " + CLOUDFLARE_HOST);
        secureClient.println("Content-Type: text/plain");
        secureClient.println("CF-Access-Client-Id: " + CF_CLIENT_ID);
        secureClient.println("CF-Access-Client-Secret: " + CF_CLIENT_SECRET);
        secureClient.println("Content-Length: " + String(configJsonPayload.length()));
        secureClient.println("Connection: close");
        secureClient.println();
        secureClient.print(configJsonPayload);

        lastAdminPayload = configJsonPayload;

        unsigned long secureBreakoutWatchdogTimer = millis();
        while (secureClient.connected() && (millis() - secureBreakoutWatchdogTimer < 1500)) {
            if (secureClient.available()) {
                String responseLine = secureClient.readStringUntil('\n');
                responseLine.trim();
                if (responseLine.length() == 0) {
                    break;
                }
            }
        }

        while (secureClient.available()) { 
            secureClient.read(); 
        }

        secureClient.stop();
        Serial.println("--> [WAN HTTPS CONFIG COMPLETE]: Persistent cloud identities populated successfully.");
        return true;
    } else {
        Serial.println("--> [WAN HTTPS CONFIG ERROR]: Handshake aborted. Vaults un-hydrated.");
    }

    return false;
}

void writeLog(String txt) {
    String timestampPrefixString = "";
    RTCTime currentSystemClockTime;

    if (RTC.getTime(currentSystemClockTime)) {
        char timestampClockBuffer[16];
        sprintf(timestampClockBuffer, "[%02d:%02d:%02d] ", 
                currentSystemClockTime.getHour(), 
                currentSystemClockTime.getMinutes(), 
                currentSystemClockTime.getSeconds());
        timestampPrefixString = String(timestampClockBuffer);
    } 
    else {
        unsigned long totalUptimeSeconds = millis() / 1000;
        unsigned long currentSeconds = totalUptimeSeconds % 60;
        unsigned long currentMinutes = (totalUptimeSeconds / 60) % 60;
        unsigned long currentHours   = (totalUptimeSeconds / 3600) % 24;

        char relativeClockBuffer[16];
        sprintf(relativeClockBuffer, "[+%02ld:%02ld:%02ld] ", currentHours, currentMinutes, currentSeconds);
        timestampPrefixString = String(relativeClockBuffer);
    }

    String finalizedTimestampedLogLine = timestampPrefixString + txt;

    Serial.println(finalizedTimestampedLogLine);

    if (BLE.connected()) {
        txCharacteristic.setValue(finalizedTimestampedLogLine);
    }

    String cleanLogLine = finalizedTimestampedLogLine;
    cleanLogLine.trim();

    if (cleanLogLine.length() > 256) {
        cleanLogLine = cleanLogLine.substring(0, 253) + "...";
    }

    systemLogBufferArray[currentLogWritePointerIndex] = cleanLogLine;
    currentLogWritePointerIndex = (currentLogWritePointerIndex + 1) % MAX_SYSTEM_LOGS;
}
