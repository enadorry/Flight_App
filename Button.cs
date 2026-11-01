using UnityEngine;
using TMPro;
using UnityEngine.InputSystem;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Collections;


public class Button : MonoBehaviour
{
    int FlightPWR = 0;
    int send = 0;
    private const int port = 8000; 
    private const string host = "192.168.4.1";
    private UdpClient client;
    float nextTime;
    public TextMeshProUGUI FlightPWR_text;
    public TextMeshProUGUI Flight_Mode;
    //多分これより上のプログラムはいじらないほうがいい
    bool EMERGENCY = false;
    bool START = false;
    void Start()
    {
        client = new UdpClient();
        client.Connect(host, port); //ESP32の192.168.11.1/8000に接続する
        Application.targetFrameRate = 120; //画面リフレッシュレートを120hzにする
    }
    int sentbinary;
    bool rightstickup;
    bool rightstickdown;
    public int FlightSpeedControll(int change_power)
    {
        if (FlightPWR + change_power >= 870 || FlightPWR + change_power<0)
        {
            return FlightPWR;
        }else{return FlightPWR + change_power;}
        
    }
    
    void Update()
    {
        if (Keyboard.current.spaceKey.isPressed && !EMERGENCY)
        {
            send = send + 1;
            EMERGENCY = true;
            Flight_Mode.text = "EMERGENCY_MODE!";
        }
        if(Gamepad.current != null)
        {
            if (Gamepad.current.dpad.up.wasPressedThisFrame)
            {
                FlightPWR=FlightSpeedControll(50);
                
            }
            else if (Gamepad.current.dpad.down.wasPressedThisFrame)
            {
                FlightPWR=FlightSpeedControll(-50);
            }
            /*
            else if (Gamepad.current.dpad.left.isPressed)
            {
                send = send + 32;
            }
            else if (Gamepad.current.dpad.right.isPressed)
            {
                send = send + 16;
            }
            if (Gamepad.current.rightShoulder.isPressed)
            {
                send = send + 8;
            }
            else if (Gamepad.current.rightTrigger.isPressed)
            {
                send = send + 4;
            }
            */
            if (Gamepad.current.buttonNorth.isPressed && !START)
            {
                send = send + 2;
                START = true;
                Flight_Mode.text = "FLIGHT_MODE";
            }
            else if (Gamepad.current.buttonSouth.isPressed && !EMERGENCY)
            {
                send = send + 1;
                EMERGENCY = true;
                Flight_Mode.text = "EMERGENCY_MODE!";
            }
            
            //Debug.Log(FlightPWR);
            FlightPWR_text.text = FlightPWR.ToString();
            
        }
        if (Time.realtimeSinceStartup >= nextTime)
        {
            nextTime = Time.realtimeSinceStartup + 0.15f;
            var message = Encoding.UTF8.GetBytes(FlightPWR.ToString("000")+send);
            client.Send(message,message.Length);
            Debug.Log(send);
            START = false;
            EMERGENCY = false;
            send = 0;

        }
        
    }
}

//this is make by enadorry!
//thx for H2O team