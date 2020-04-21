using System;
using System.Collections.Generic;
//using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;

namespace Reader
{
    public delegate void MessageReceivedEventHandler(byte[] btAryBuffer);

    interface ITalker
    {
        event MessageReceivedEventHandler MessageReceived;    
        bool Connect(IPAddress ip, int port, out string strException);                 
        bool SendMessage(byte[] btAryBuffer);                 
        void SignOut();                                       

        bool IsConnect();                                   
    }
}
