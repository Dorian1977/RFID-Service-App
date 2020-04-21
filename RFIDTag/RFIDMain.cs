#define SCAN2READ
#define READ2SCAN
#define READ2ERASE //skip erase tag

using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.IO.Ports;
using System.IO;
using System.Timers;
using System.Threading;
using System.Linq;

namespace RFIDTag
{    
    public class RFIDMain
    {
        Reader.ReaderMethod reader;

        ReaderSetting m_curSetting = new ReaderSetting();
        InventoryBuffer m_curInventoryBuffer = new InventoryBuffer();
        OperateTagBuffer m_curOperateTagBuffer = new OperateTagBuffer();
        Symmetric_Encrypted symmetric = new Symmetric_Encrypted();

        const short m_nRealRate = 3; //20;
        const short rwTagDelay = 300; //80ms 3-5 failed, 90ms 2-3 failed, 100ms 2-10 failed

        //authorization ink time stamp 2 days allowed
        //also record in log file, and verified it everytime read it
        const int dayAllowed = 2; 

        int m_nReceiveFlag_3S = 0;
        int m_nTotal = 0;
        bool m_bInventory = false;

        public System.Timers.Timer timerResetLED;
        private static Thread threadInventory;
        List<List<string>> tagLists = new List<List<string>>();

        static LEDStatus resetGreenLED = LEDStatus.GreenOff;
        static LEDStatus resetRedLED = LEDStatus.RedOff;
        int iComPortStatus = 0;

        enum LEDStatus
        {
            Off,
            Green,
            GreenOff,
            Red,
            RedOff,
            Idle
        }

        readTagStatus tagState = readTagStatus.Reading;
        enum readTagStatus
        {
            Reading,
            ReadReserve,
            ReadReserveOK,
            VerifiedSuccessful,
            VerifiedFailed,
            Erasing,
            EraseConfirmed,
            ZeroData
        }

        public RFIDMain()
        {
            StartThread();
        }

        public void RFIDStop()
        {
            setLEDstaus(LEDStatus.Off);
            setLEDstaus(LEDStatus.Off);
            reader.resetCom();
            reader.CloseCom();
        }

        public void StartThread()
        {            
            threadInventory = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        scanInventory();
                        Thread.Sleep(500);                      
                    }
                    catch (ThreadInterruptedException) { }
                }
            });
            threadInventory.Start();

            timerResetLED = new System.Timers.Timer(4000);
            timerResetLED.Elapsed += OnTimerResetLED;
            timerResetLED.AutoReset = false;
            timerResetLED.Enabled = false;
        }

        void OnTimerResetLED(object sender, ElapsedEventArgs e)
        {
            setLEDstaus(LEDStatus.Off);
            //timerResetLED.Stop();  //auto stop      
        }

        void scanInventory()
        {
            m_nReceiveFlag_3S++;
            if (m_nReceiveFlag_3S >= 5)
            {
                string encryptedData = "";
                RunLoopInventroy();
                m_nReceiveFlag_3S = 0;

                try
                {
                    if (RFIDTagInfo.readInkAuthFile(out encryptedData))
                    {
                        byte[] cipherText = System.Convert.FromBase64String(encryptedData);
                        string plaintext = symmetric.DecryptData(cipherText);

                        if (plaintext != "" && RFIDTagInfo.checkAuthDateAndLog(plaintext))
                            RFIDTagInfo.addTempInkAuth();
                    }
                }
                catch (Exception exp) { }              
            }
        }

        public bool run()
        {
            reader = new Reader.ReaderMethod();
            reader.AnalyCallback = AnalyData;

            //load serial port
            string[] serialPort = SerialPort.GetPortNames();
            int i = serialPort.Length - 1;

            do
            {               
                if (iComPortStatus != 1)
                {
                    string comPort = serialPort[i];
                    checkPort(comPort);
                    Thread.Sleep(rwTagDelay);
                }
            }
            while ((iComPortStatus != 1) && (i-- > 0));

            if(iComPortStatus == 1)
            {
                Console.WriteLine("Load setting");
                loadSetting();
                return true;
            }
            else
            {
                Console.WriteLine("Error, can't connect to any Com Port");
            }
            return false;
        }

        public void checkPort(string comPort)
        {
            string strException = string.Empty;
            int nRet = reader.OpenCom(comPort, Convert.ToInt32("115200"), out strException);
            if (nRet != 0)
            {
                string strLog = "Connection failed, failure cause: " + strException;
                Console.WriteLine(strLog);
                return;
            }
            else
            {
                string strLog = "Connect " + comPort + "@" + "115200";
                Console.WriteLine(strLog);
            }
            
            reader.resetCom();
            Thread.Sleep(rwTagDelay);
            reader.GetFirmwareVersion(m_curSetting.btReadId);
            Thread.Sleep(rwTagDelay*2);
        }

        public void loadSetting()
        { //R2000UartDemo_Load
            string path = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            RFIDTagInfo.loadVolumeFilePath(path + "\\AEWA\\APRINT\\pIsNmK_a40670.dat");
            RFIDTagInfo.loadinkAuthFilePath("C:\\RFIDAuthorization.dat");
            
            //load Access Code
            byte[] accessCode = Properties.Resources.AccessCode;
            string strCode = RFIDTagInfo.ASCIIToHex(Encoding.ASCII.GetString(accessCode)).ToUpper();
            symmetric.loadAccessCode(strCode);
            Console.WriteLine("Read Access code " + strCode);

            //load key
            byte[] encryptKey = Properties.Resources.SymmetricKey;
            symmetric.loadKey(encryptKey);

            //load LabelFormat":
            byte[] labelFormat = Properties.Resources.LabelFormat;
            RFIDTagInfo.loadLabelFormat(labelFormat);

            //init scan tag buffer
            m_curInventoryBuffer.ClearInventoryPar();
            m_curInventoryBuffer.btRepeat = 1; //1
            m_curInventoryBuffer.bLoopCustomizedSession = false;

            m_bInventory = true;
            m_curInventoryBuffer.bLoopInventory = true;
            m_curInventoryBuffer.bLoopInventoryReal = true;
            m_curInventoryBuffer.ClearInventoryResult();
            
            tagLists.Clear();
            m_nTotal = 0;
            reader.SetWorkAntenna(m_curSetting.btReadId, 0x00);
            m_curSetting.btWorkAntenna = 0x00;
            tagState = readTagStatus.Reading;
            return;
        }

        public void resetScanBuffer()
        {
            tagLists.Clear();
            m_nTotal = 0;
            m_curInventoryBuffer.ClearInventoryResult(); // reset timer

            if ((resetRedLED == LEDStatus.Red) ||
                (resetGreenLED == LEDStatus.Green))
            {
                timerResetLED.Enabled = true;
                timerResetLED.Start();
            }
            else
            {
                setLEDstaus(LEDStatus.Off);
            }
        }

        void ProcessGetFirmwareVersion(Reader.MessageTran msgTran)
        {
            string strCmd = "Get Reader's firmware version";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 2)
            {
                m_curSetting.btMajor = msgTran.AryData[0];
                m_curSetting.btMinor = msgTran.AryData[1];
                m_curSetting.btReadId = msgTran.ReadId;

                iComPortStatus = 1;
                //RefreshReadSetting(msgTran.Cmd);
                //Console.WriteLine(strCmd);
                Console.WriteLine("Connected com port successful");
                return;
            }
            else if (msgTran.AryData.Length == 1)
            {
                strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[0]);
            }
            else
            {                
                strErrorCode = "Unknown Error";
            }

            iComPortStatus = -1;
            string strLog = strCmd + "Failure, failure cause: " + strErrorCode;
            Console.WriteLine(strLog);
        }

        void ProcessGetOutputPower(Reader.MessageTran msgTran)
        {
            string strCmd = "Get RF Output Power";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                m_curSetting.btReadId = msgTran.ReadId;
                m_curSetting.btOutputPower = msgTran.AryData[0];

                //RefreshReadSetting(0x77);
                m_curSetting.btOutputPower = 0;
                m_curSetting.btOutputPowers = null;
                //Console.WriteLine(strCmd);
                return;
            }
            else if (msgTran.AryData.Length == 8)
            {
                m_curSetting.btReadId = msgTran.ReadId;
                m_curSetting.btOutputPowers = msgTran.AryData;

                //RefreshReadSetting(0x97);
                //Console.WriteLine(strCmd);
                return;
            }
            else if (msgTran.AryData.Length == 4)
            {
                m_curSetting.btReadId = msgTran.ReadId;
                m_curSetting.btOutputPowers = msgTran.AryData;                 

                //RefreshReadSetting(0x77);
                //Console.WriteLine(strCmd);
                return;
            }
            else
            {
                strErrorCode = "Unknown Error";
            }

            string strLog = strCmd + "Failure, failure cause: " + strErrorCode;
            Console.WriteLine(strLog);
        }

        void ProcessGetFrequencyRegion(Reader.MessageTran msgTran)
        {
            string strCmd = "Query RF frequency spectrum    ";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 3)
            {
                m_curSetting.btReadId = msgTran.ReadId;
                m_curSetting.btRegion = msgTran.AryData[0];
                m_curSetting.btFrequencyStart = msgTran.AryData[1];
                m_curSetting.btFrequencyEnd = msgTran.AryData[2];

                //RefreshReadSetting(0x79);
                //Console.WriteLine(strCmd);
                return;
            }
            else if (msgTran.AryData.Length == 6)
            {
                m_curSetting.btReadId = msgTran.ReadId;
                m_curSetting.btRegion = msgTran.AryData[0];
                m_curSetting.btUserDefineFrequencyInterval = msgTran.AryData[1];
                m_curSetting.btUserDefineChannelQuantity = msgTran.AryData[2];
                m_curSetting.nUserDefineStartFrequency = msgTran.AryData[3] * 256 * 256 + msgTran.AryData[4] * 256 + msgTran.AryData[5];
                //RefreshReadSetting(0x79);
                //Console.WriteLine(strCmd);
                return;
            }
            else if (msgTran.AryData.Length == 1)
            {
                strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[0]);
            }
            else
            {
                strErrorCode = "Unknown Error";
            }

            string strLog = strCmd + "Failure, failure cause: " + strErrorCode;
            Console.WriteLine(strLog);
        }

        bool ProcessWriteGpioValue(Reader.MessageTran msgTran)
        {
            string strCmd = "Set GPIO status";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                if (msgTran.AryData[0] == 0x10)
                {
                    m_curSetting.btReadId = msgTran.ReadId;
                    //Console.WriteLine(strCmd);
                    return true;
                }
                else
                {
                    strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[0]);
                }
            }
            else
            {
                strErrorCode = "Unknown Error";
            }

            string strLog = strCmd + "Failure, failure cause: " + strErrorCode;
            Console.WriteLine(strLog);
            return false;
        }

        void RunLoopInventroy()
        {
            if (m_curInventoryBuffer.nIndexAntenna < m_curInventoryBuffer.lAntenna.Count - 1 || m_curInventoryBuffer.nCommond == 0)
            {
                if (m_curInventoryBuffer.nCommond == 0)
                {
                    m_curInventoryBuffer.nCommond = 1;

                    if (m_curInventoryBuffer.bLoopInventoryReal)
                    {
                        //m_bLockTab = true;
                        //btnInventory.Enabled = false;
                        if (m_curInventoryBuffer.bLoopCustomizedSession)
                        {
                            //reader.CustomizedInventory(m_curSetting.btReadId, m_curInventoryBuffer.btSession, m_curInventoryBuffer.btTarget, m_curInventoryBuffer.btRepeat); 
                            reader.CustomizedInventoryV2(m_curSetting.btReadId, m_curInventoryBuffer.CustomizeSessionParameters.ToArray());
                        }
                        else
                        {//enter here
                            reader.InventoryReal(m_curSetting.btReadId, m_curInventoryBuffer.btRepeat);
                        }
                    }
                    else
                    {
                        if (m_curInventoryBuffer.bLoopInventory)
                            reader.Inventory(m_curSetting.btReadId, m_curInventoryBuffer.btRepeat);
                    }
                }
                else
                {
                    m_curInventoryBuffer.nCommond = 0;
                    m_curInventoryBuffer.nIndexAntenna++;

                    //byte btWorkAntenna = 0; //m_curInventoryBuffer.lAntenna[m_curInventoryBuffer.nIndexAntenna];
                    //reader.SetWorkAntenna(m_curSetting.btReadId, btWorkAntenna); 
                    //m_curSetting.btWorkAntenna = btWorkAntenna;
                }
            }
            else if (m_curInventoryBuffer.bLoopInventory)
            {//enter here
                m_curInventoryBuffer.nIndexAntenna = 0;
                m_curInventoryBuffer.nCommond = 0;

                //byte btWorkAntenna = 0; // m_curInventoryBuffer.lAntenna[m_curInventoryBuffer.nIndexAntenna];
                //reader.SetWorkAntenna(m_curSetting.btReadId, btWorkAntenna);
                //m_curSetting.btWorkAntenna = btWorkAntenna;
            }            
            Thread.Sleep(10);
        }

        void ProcessSetWorkAntenna(Reader.MessageTran msgTran)
        {
            int intCurrentAnt = 0;
            intCurrentAnt = m_curSetting.btWorkAntenna + 1;
            string strCmd = "Set working antenna successfully, Current Ant: Ant" + intCurrentAnt.ToString();

            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                if (msgTran.AryData[0] == 0x10)
                {
                    m_curSetting.btReadId = msgTran.ReadId;
                    //Console.WriteLine(strCmd);

                    if (m_bInventory)
                    {
                        RunLoopInventroy();
                    }
                    return;
                }
                else
                {
                    strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[0]);
                }
            }
            else
            {
                strErrorCode = "Unknown Error";
            }

            string strLog = strCmd + "Failure, failure cause: " + strErrorCode;
            Console.WriteLine(strLog);

            if (m_bInventory)
            {
                m_curInventoryBuffer.nCommond = 1;
                m_curInventoryBuffer.dtEndInventory = DateTime.Now;
                RunLoopInventroy();
            }
        }

        void AnalyData(Reader.MessageTran msgTran)
        {
            m_nReceiveFlag_3S = 0;
            if (msgTran.PacketType != 0xA0)
            {
                return;
            }
            switch (msgTran.Cmd)
            {
                case 0x72:
                    ProcessGetFirmwareVersion(msgTran);
                    break;
                case 0x74:
                    ProcessSetWorkAntenna(msgTran);
                    break;
                case 0x97:
                case 0x77:
                    ProcessGetOutputPower(msgTran);
                    break;
                case 0x79:
                    ProcessGetFrequencyRegion(msgTran);
                    break;
                case 0x61:
                    ProcessWriteGpioValue(msgTran);
                    break;
                case 0x81:
                    {
                        //byte btMemBank = 3; // 0
                        //byte btWordAddr = 0; // 0
                        //byte btWordCnt = 20; // 4
                        bool bReadSuccess = ProcessReadTag(msgTran);
                        byte[] btAryPwd = RFIDTagInfo.GetPassword(symmetric.readAccessCode(), 2);

                        if (!bReadSuccess)
                        {
                            if (readTagRetry++ < rwTagRetryMAX)
                            {
                                //Thread.Sleep(rwTagDelay);
                                switch (tagState)
                                {
                                    case readTagStatus.ReadReserve:
                                        reader.ReadTag(m_curSetting.btReadId, 0, 0, 4, btAryPwd);
                                        break;
                                    default:
                                        reader.ReadTag(m_curSetting.btReadId, 3, 0, 22, null); //btAryPwd);
                                        break;
                                }                                                         
                                Console.WriteLine("Read Tag retry (" + readTagRetry + "), state " + 
                                                  tagState.ToString());
                                Thread.Sleep(rwTagDelay); //set to 20 will delay the respond
                                break;
                            }
                            else
                            {                                 
                                Console.WriteLine("Read tag failed" + ", state " +
                                                  tagState.ToString());
                                readTagRetry = 0;
                                setLEDstaus(LEDStatus.Red);
                               
                            }   
                        }
                        else
                        {
                            if(tagState == readTagStatus.ReadReserveOK)
                            {
                                //Thread.Sleep(rwTagDelay);
                                reader.ReadTag(m_curSetting.btReadId, 3, 0, 22, null); //btAryPwd);
                                Thread.Sleep(rwTagDelay);
                                Console.WriteLine("Read Tag ok" +
                                                  ", state " + tagState.ToString());
                            }
                            readTagRetry = 0;
                        }
#if READ2SCAN
                        //if(tagState != readTagStatus.Erasing)
                        //if(readTagRetry++ >= rwTagRetryMAX)
                            resetScanBuffer();
#endif
                    }
                    break;
                                      
                case 0x82:
                case 0x94:
                    {
                        bool bWriteSuccess = ProcessWriteTag(msgTran); 
                        if(!bWriteSuccess)
                        {
                            if(writeTagRetry++ < rwTagRetryMAX)
                            {
                                if (writeZeroTag(3, 0, 22) != 0)
                                    bWriteSuccess = false;

                                Console.WriteLine("Write Tag retry " + writeTagRetry +
                                    ", state " + tagState.ToString());
                                Thread.Sleep(rwTagDelay);
                                break;
                            }
                            else
                            {                                
                                Console.WriteLine("Write tag failed" +
                                    ", state " + tagState.ToString());
                                writeTagRetry = 0;                                
                                setLEDstaus(LEDStatus.Red);                                                              
                            }                            
                        }
                        else
                        {
                            Console.WriteLine("Write Tag successful" +
                                ", state " + tagState.ToString());
                            writeTagRetry = 0;
                        }
#if READ2SCAN
                        //return to scan
                        //if (tagState != readTagStatus.Erasing)
                        //if (writeTagRetry++ >= rwTagRetryMAX)
                            resetScanBuffer();
#endif
                    }
                    break;             
                case 0x89:
                case 0x8B:
                    ProcessInventoryReal(msgTran);
                    break;
            }
        }
        const short writeMAXCnt = 2;
        static short writeCnt = 0;
        static int writeSuccessed = 0;
        static int writeFailed = 0;
        public void WriteLEDGPIO(byte btReadId, byte btChooseGpio, byte btGpioValue)
        {
            int bWriteResult = -1;
            do
            {
                bWriteResult = reader.WriteGpioValue(btReadId, btChooseGpio, btGpioValue);

                if (bWriteResult == 0)
                    writeSuccessed++;
                else
                {
                    writeFailed++;
                    Console.Write("Retry write GPIO " + writeCnt +
                                  " rate " + writeFailed + " (" + writeSuccessed + ")");                   
                }
                Thread.Sleep(50);
            } while ((bWriteResult != 0) && writeCnt < writeMAXCnt);           
        }

        private void setLEDstaus(LEDStatus ledStatus)
        {//GPIO 4 green, GPIO 3 red
            switch (ledStatus)
            {
                case LEDStatus.GreenOff:
                    {
                        WriteLEDGPIO(m_curSetting.btReadId, 0x04, 0);
                        m_curSetting.btGpio4Value = 0;
                        Console.WriteLine("LED Green OFF ");
                        resetGreenLED = ledStatus;
                    }
                    return;
                case LEDStatus.RedOff:
                    {
                        WriteLEDGPIO(m_curSetting.btReadId, 0x03, 0);
                        m_curSetting.btGpio3Value = 0;
                        Console.WriteLine("LED Red OFF, ");
                        resetRedLED = ledStatus;
                    }
                    return;
                case LEDStatus.Off:
                    {
                        //if (resetGreenLED == LEDStatus.Green)
                        {
                            WriteLEDGPIO(m_curSetting.btReadId, 0x04, 0);
                            m_curSetting.btGpio4Value = 0;
                            Console.Write("LED Green OFF ");
                            resetGreenLED = LEDStatus.Off;
                            Thread.Sleep(rwTagDelay);
                        }
                        //if (resetRedLED == LEDStatus.Red)
                        {
                            WriteLEDGPIO(m_curSetting.btReadId, 0x03, 0);
                            m_curSetting.btGpio3Value = 0;
                            Console.WriteLine("LED Red OFF, ");
                            resetRedLED = LEDStatus.RedOff;
                        }
                    }
                    break;
                case LEDStatus.Red:
                    {
                       // if ((m_curSetting.btGpio4Value == 1 ) ||
                       //     (resetGreenLED == LEDStatus.Green))
                        {
                            m_curSetting.btGpio4Value = 0;
                            resetGreenLED = LEDStatus.GreenOff;
                            WriteLEDGPIO(m_curSetting.btReadId, 0x04, 0);
                            Thread.Sleep(rwTagDelay);
                            Console.Write("LED Green Off, ");
                        }
                        WriteLEDGPIO(m_curSetting.btReadId, 0x03, 1);
                        m_curSetting.btGpio3Value = 1;
                        Console.Write("LED Red ON, ");
                        resetRedLED = ledStatus;
                    }
                    break;
                case LEDStatus.Green:
                    {
                       // if((m_curSetting.btGpio3Value == 1) ||
                       //    (resetGreenLED == LEDStatus.Red))
                        {
                            m_curSetting.btGpio3Value = 0;
                            resetRedLED = LEDStatus.RedOff;
                            WriteLEDGPIO(m_curSetting.btReadId, 0x03, 0);
                            Thread.Sleep(rwTagDelay);
                            Console.Write("LED Red Off, ");
                        }
                        WriteLEDGPIO(m_curSetting.btReadId, 0x04, 1);
                        m_curSetting.btGpio4Value = 1;
                        Console.WriteLine("LED Green ON ");
                        resetGreenLED = ledStatus;
                    }
                    break;
                case LEDStatus.Idle:
                    {
                        //if (resetRedLED == LEDStatus.Red)
                        {
                            WriteLEDGPIO(m_curSetting.btReadId, 0x03, 0);
                            m_curSetting.btGpio3Value = 0;
                            Console.Write("LED Red OFF, ");
                            resetRedLED = LEDStatus.RedOff;
                            Thread.Sleep(rwTagDelay);
                        }

                        m_curSetting.btGpio4Value = 1;
                        WriteLEDGPIO(m_curSetting.btReadId, 0x04, 1);                       
                        Console.Write("LED Green ON, ");
                        Thread.Sleep(rwTagDelay);

                        m_curSetting.btGpio4Value = 0;
                        resetGreenLED = LEDStatus.GreenOff;
                        WriteLEDGPIO(m_curSetting.btReadId, 0x04, 0);
                        Console.WriteLine("LED Green OFF ");
                    }
                    break;
            }            
            Thread.Sleep(rwTagDelay);
        }

        private void verifyTag(string data)
        {
            //Console.WriteLine("Load Key " + symmetric.readKey());
            if (data.StartsWith(" "))
                data = data.Remove(0, 1) + " ";
                        
            if (tagState == readTagStatus.ReadReserve)
            {
                if (data.Substring(12) == symmetric.readAccessCode())
                {
                    tagInfo.tagReserve = data.Substring(0, 12);
                    tagState = readTagStatus.ReadReserveOK;
                }
                return;
            }
            else
            {
                if(data.Length < 120)
                {
                    return;
                }
            }

            tagInfo.tagData = data;
            if (tagState == readTagStatus.Erasing)
            {         
                if(RFIDTagInfo.verifyData(data, false, true))
                {
                    tagState = readTagStatus.EraseConfirmed;
                    Console.WriteLine("Verified Erase data successful, " + data);                    
                }
                else
                {//re-erase tag                    
                    Console.WriteLine("Verified Erase data failed, " + data);
                }
                return;
            }

            string decryptMsg = symmetric.DecryptFromHEX(tagInfo.tagReserve, tagInfo.tagData);
            if (decryptMsg != "")
            {
                //Console.WriteLine("Decrypt data " + decryptMsg + ", Size " + decryptMsg.Length);
                tagInfo.tagDecryptedInfo = decryptMsg;
                if (RFIDTagInfo.verifyData(decryptMsg, true, false))
                {
                    tagState = readTagStatus.VerifiedSuccessful;
                    Console.WriteLine("Verified data successful, " + decryptMsg);
                }
                else
                {
                    if(tagState == readTagStatus.ReadReserveOK)
                        tagState = readTagStatus.VerifiedFailed;

                    Console.WriteLine("Verified data failed, " + decryptMsg + 
                                      ", state " + tagState.ToString());
                }
            }
            else if (tagState != readTagStatus.ZeroData)
            {
                tagState = readTagStatus.VerifiedFailed;
                Console.WriteLine("Read data failed, state " + tagState.ToString());
            }
        }

        bool ProcessReadTag(Reader.MessageTran msgTran)
        {
            string strCmd = "Read Tag";
            string strErrorCode = string.Empty;

            try
            {
                //Console.WriteLine("Read Tag, AryData.Length " + msgTran.AryData.Length, 0);
                if (msgTran.AryData.Length == 1)
                {
                    strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[0]);
                    Console.WriteLine(strCmd + " Failure, read tag failure cause1: " + strErrorCode);
                    return false;
                }
                else
                {                    
                    int nLen = msgTran.AryData.Length;
                    int nDataLen = Convert.ToInt32(msgTran.AryData[nLen - 3]);
                    int nEpcLen = Convert.ToInt32(msgTran.AryData[2]) - nDataLen - 4;

                    string strPC = CCommondMethod.ByteArrayToString(msgTran.AryData, 3, 2);
                    string strEPC = CCommondMethod.ByteArrayToString(msgTran.AryData, 5, nEpcLen);
                    string strCRC = CCommondMethod.ByteArrayToString(msgTran.AryData, 5 + nEpcLen, 2);
                    string strData = CCommondMethod.ByteArrayToString(msgTran.AryData, 7 + nEpcLen, nDataLen);

                    byte byTemp = msgTran.AryData[nLen - 2];
                    byte byAntId = (byte)((byTemp & 0x03) + 1);
                    string strAntId = byAntId.ToString();
                    string strReadCount = msgTran.AryData[nLen - 1].ToString();

                    for (int i = 0; i < tagLists.Count; i++)
                    {
                        if (tagLists[i][0] == strEPC)
                        {//find the tag, then add the data
                            tagLists[i][1] = strData;
                            break;
                        }
                    }

                    //Console.WriteLine("Read data " + strData + 
                    //                  " state " + tagState.ToString());
                    //tagInfo.label = RFIDTagInfo.readEPCLabel(strEPC);                                  

                    //read data section
                    try
                    {
                        verifyTag(strData);
                        Console.WriteLine(" State: " + tagState.ToString());
                        switch (tagState)
                        {
                            case readTagStatus.EraseConfirmed:
                                {
                                    //1. Erase verified success tag
                                    //2. add ink volume     
                                    string path = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                                    RFIDTagInfo.addLog(path + "\\RFIDTag");
                                    
                                    setLEDstaus(LEDStatus.Green); //green on
                                    tagInfo.bVerified = true;
                                    tagState = readTagStatus.ZeroData;
                                    break; // return true;
                                }

                            case readTagStatus.VerifiedSuccessful:
                                {
#if READ2ERASE                                                                      
                                    string[] tempList = tagInfo.tagDecryptedInfo.Split(RFIDTagInfo.serialSep);
                                    int intToneVolume = Int32.Parse(tempList[1].Substring(4, 4));
                                    if(RFIDTagInfo.addVolumeToFile(intToneVolume))
                                    {
                                        tagState = readTagStatus.Erasing;
                                        writeZeroTag(3, 0, 22);
                                        Console.WriteLine("Erasing data ");
                                        Thread.Sleep(rwTagDelay);
                                    }
                                    else
                                    {
                                        setLEDstaus(LEDStatus.Idle);
                                        return false;
                                    }
#else
                                    string[] tempList = tagInfo.tagDecryptedInfo.Split(RFIDTagInfo.serialSep);
                                    int intToneVolume = Int32.Parse(tempList[1].Substring(4, 4));
                                    if (RFIDTagInfo.addVolumeToFile(intToneVolume))
                                    {
                                        RFIDTagInfo.addLog();
                                        setLEDstaus(LEDStatus.Green); //green on
                                        tagInfo.bVerified = true;
                                        tagState = readTagStatus.ZeroData;
                                    }
#endif
                                    break; // return true;
                                }

                            case readTagStatus.ReadReserve:
                            case readTagStatus.VerifiedFailed:
                                return false;
                            default:
                                break;
                        }
                        return true;
                            
                    }
                    catch (Exception exp)
                    {
                        Console.WriteLine(strCmd + " tag " + strEPC 
                                            + " got error " + exp.Message +
                                            ", state " + tagState.ToString());
                        return false;
                    }
                }                  
                          
            }
            catch (Exception exp)
            {
                //setLEDstaus(LEDStatus.Red); //red on      
                Console.WriteLine(strCmd + " got error " + exp.Message +
                                  ", state " + tagState.ToString());
                return false;
            }            
        }
     
        private int writeZeroTag(byte btMemBank, byte btWordAddr, byte btWordCnt)
        {//user section to zero   //3,0,22
            byte btCmd = 0x94;
            byte[] zeroTmp = Enumerable.Repeat((byte)0x00, btWordCnt*2).ToArray();
            byte[] btAryPwd = RFIDTagInfo.GetPassword(symmetric.readAccessCode(), 2);
            return reader.WriteTag(m_curSetting.btReadId, btAryPwd, btMemBank, 
                                   btWordAddr, btWordCnt, zeroTmp, btCmd);            
        }
                
        private const int rwTagRetryMAX = 5;
        static int readTagRetry = 0;
        static int writeTagRetry = 0;
        private string ellapsed;

        bool ProcessWriteTag(Reader.MessageTran msgTran)
        {
            string strCmd = "Write Tag";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[0]);
                Console.WriteLine(strCmd + " Failure, failure cause1: " + strErrorCode);
                return false;
            }
            else
            {
                int nLen = msgTran.AryData.Length;
                int nEpcLen = Convert.ToInt32(msgTran.AryData[2]) - 4;

                if (msgTran.AryData[nLen - 3] != 0x10)
                {
                    strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[nLen - 3]);                 
                    Console.WriteLine(strCmd + " tag " +
                                    " Failure, failure cause2: " + strErrorCode);
                    return false;
                }

                string strPC = CCommondMethod.ByteArrayToString(msgTran.AryData, 3, 2);
                string strEPC = CCommondMethod.ByteArrayToString(msgTran.AryData, 5, nEpcLen);
                string strCRC = CCommondMethod.ByteArrayToString(msgTran.AryData, 5 + nEpcLen, 2);
                string strData = string.Empty;

                byte byTemp = msgTran.AryData[nLen - 2];
                byte byAntId = (byte)((byTemp & 0x03) + 1);
                string strAntId = byAntId.ToString();
                string strReadCount = msgTran.AryData[nLen - 1].ToString();

                DataRow row = m_curOperateTagBuffer.dtTagTable.NewRow();
                for (int i = 0; i < tagLists.Count; i++)
                {
                    if (tagLists[i][0] == row[2].ToString())
                    {//find the tag, then add the data
                        tagLists[i][1] = row[3].ToString();
                    }
                }

                //erase success, then read back tag
                if (tagState == readTagStatus.Erasing)
                {
                    byte[] btAryPwd = RFIDTagInfo.GetPassword(symmetric.readAccessCode(), 2);
                    reader.ReadTag(m_curSetting.btReadId, 3, 0, 22, btAryPwd);
                    Thread.Sleep(50);
                }
                return true;
            }
        }

        string GetFreqString(byte btFreq)
        {
            string strFreq = string.Empty;

            if (m_curSetting.btRegion == 4)
            {
                float nExtraFrequency = btFreq * m_curSetting.btUserDefineFrequencyInterval * 10;
                float nstartFrequency = ((float)m_curSetting.nUserDefineStartFrequency) / 1000;
                float nStart = nstartFrequency + nExtraFrequency / 1000;
                string strTemp = nStart.ToString("0.000");
                return strTemp;
            }
            else
            {
                if (btFreq < 0x07)
                {
                    float nStart = 865.00f + Convert.ToInt32(btFreq) * 0.5f;
                    string strTemp = nStart.ToString("0.00");
                    return strTemp;
                }
                else
                {
                    float nStart = 902.00f + (Convert.ToInt32(btFreq) - 7) * 0.5f;
                    string strTemp = nStart.ToString("0.00");
                    return strTemp;
                }
            }
        }

        //private static bool bLEDOn = false;
        //case 0x89: 0x8B:
        void ProcessInventoryReal(Reader.MessageTran msgTran)
        {//call RefreshInventoryReal, RunLoopInventroy
            string strCmd = "";
            if (msgTran.Cmd == 0x89)
            {
                strCmd = "Real time inventory scan";
            }
            if (msgTran.Cmd == 0x8B)
            {
                strCmd = "User define Session and Inventoried Flag inventory";
            }
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[0]);
                string strLog = strCmd + " Failure, failure cause: " + strErrorCode;
                Console.WriteLine(strLog);

                m_curInventoryBuffer.dtEndInventory = DateTime.Now;
                RefreshInventoryReal(0x89);
                if (m_bInventory)
                {
                    RunLoopInventroy();
                }
            }
            else if (msgTran.AryData.Length == 7)
            {
                m_curInventoryBuffer.nReadRate = Convert.ToInt32(msgTran.AryData[1]) * 256 + 
                                                 Convert.ToInt32(msgTran.AryData[2]);
                m_curInventoryBuffer.nDataCount = Convert.ToInt32(msgTran.AryData[3]) * 256 * 256 * 256 +
                                                  Convert.ToInt32(msgTran.AryData[4]) * 256 * 256 + 
                                                  Convert.ToInt32(msgTran.AryData[5]) * 256 + 
                                                  Convert.ToInt32(msgTran.AryData[6]);
                m_curInventoryBuffer.dtEndInventory = DateTime.Now;                
                RefreshInventoryReal(0x89);                
                if (m_bInventory)
                {
                    RunLoopInventroy();
                }
                //Console.WriteLine(strCmd);
            }
            else
            {
                m_nTotal++;
                int nLength = msgTran.AryData.Length;
                int nEpcLength = nLength - 4;


                //Add inventory list
                string strEPC = CCommondMethod.ByteArrayToString(msgTran.AryData, 3, nEpcLength);
                string strPC = CCommondMethod.ByteArrayToString(msgTran.AryData, 1, 2);
                string strRSSI = string.Empty;

                strRSSI = (msgTran.AryData[nLength - 1] & 0x7F).ToString();       

                byte btTemp = msgTran.AryData[0];
                byte btAntId = (byte)((btTemp & 0x03) + 1);
                string strPhase = string.Empty;             
                byte btFreq = (byte)(btTemp >> 2);
                string strFreq = GetFreqString(btFreq);
                m_curInventoryBuffer.nCurrentAnt = (int)btAntId;   

                DataRow[] drs = m_curInventoryBuffer.dtTagTable.Select(string.Format("COLEPC = '{0}'", strEPC));
                if (drs.Length == 0)
                {
                    DataRow row1 = m_curInventoryBuffer.dtTagTable.NewRow();                 
                    row1[0] = strPC;
                    row1[2] = strEPC;
                    row1[4] = strRSSI;
                    row1[5] = "1";
                    row1[6] = strFreq;
                    row1[7] = "1";
                    row1[8] = "0";
                    row1[9] = "0";
                    row1[10] = "0";
                    row1[11] = "0";
                    row1[12] = "0";
                    row1[13] = "0";
                    row1[14] = "0";
                    row1[15] = strPhase;                   
                    m_curInventoryBuffer.dtTagTable.Rows.Add(row1);
                    m_curInventoryBuffer.dtTagTable.AcceptChanges();
                }
                else
                {
                    foreach (DataRow dr in drs)
                    {
                        dr.BeginEdit();
                        dr[4] = strRSSI;
                        dr[5] = (Convert.ToInt32(dr[5]) + 1).ToString();                      
                        dr[6] = strFreq;                                              
                        dr[7] = (Convert.ToInt32(dr[7]) + 1).ToString();                        
                        dr[15] = strPhase;
                        dr.EndEdit();
                    }
                    m_curInventoryBuffer.dtTagTable.AcceptChanges();
                }
                                
                m_curInventoryBuffer.dtEndInventory = DateTime.Now;
                RefreshInventoryReal(0x89);
                Thread.Sleep(10);
            }
        }

        int iIdleCount = 0;
        private void RefreshInventoryReal(byte btCmd)
        {
            switch (btCmd)
            {
                case 0x89:
                case 0x8B:
                    {
                        //int nTagCount = m_curInventoryBuffer.dtTagTable.Rows.Count;
                        int nTotalRead = m_nTotal;// m_curInventoryBuffer.dtTagDetailTable.Rows.Count;
                        TimeSpan ts = m_curInventoryBuffer.dtEndInventory - m_curInventoryBuffer.dtStartInventory;
                        int nTotalTime = ts.Minutes * 60 * 1000 + ts.Seconds * 1000 + ts.Milliseconds;

                        if (m_nTotal % m_nRealRate == 1)
                        {
                            int nIndex = 0;
                            string strEPC = "";
                            foreach (DataRow row in m_curInventoryBuffer.dtTagTable.Rows)
                            {
                                //row[0] PC
                                //row[2] EPC serial number
                                //row[4] RSSI (value - 129)dBm
                                //row[6] frequency
                                //row[7] identification count
                                bool bFound = false;
                                if (tagLists.Count > 0)
                                {
                                    for (int i = 0; i < tagLists.Count; i++)
                                    {
                                        if (tagLists[i][0] == row[2].ToString())
                                        {
                                            tagLists[i][2] = row[7].ToString();
                                            tagLists[i][3] = row[4].ToString();
                                            bFound = true;
                                            break;
                                        }
                                    }
                                }

                                if (!bFound)
                                {
                                    List<string> tagInfo = new List<string>();
                                    string tagLabel = row[2].ToString();
                                    if (tagLabel.StartsWith(" "))
                                        tagLabel = tagLabel.Remove(0, 1) + " ";

                                    if (tagLabel.StartsWith("50 53"))
                                    {
                                        tagInfo.Add(row[2].ToString()); //EPC serial number
                                        tagInfo.Add(" "); //user data
                                        tagInfo.Add(row[7].ToString()); //identification
                                        tagInfo.Add(row[4].ToString()); //RSSI (value - 129)dBm
                                        tagLists.Add(tagInfo);
                                        //Console.WriteLine("Add tag: " + row[2].ToString());
                                    }
                                }
                                nIndex++;
                            }
#if SCAN2READ
                            try
                            {
                                if (tagLists.Count == 0)
                                {
                                    Console.WriteLine("Can't find tag, idle");
                                    goto Idle;
                                }
                                else //if (tagLists.Count > 0)
                                {//scan a tag, get the best <70dbm RSSI, then read the tag
                                 //verify is same as label format
                                    int readCount = 0;
                                    int readRSSI = 60;
                                    int maxCountIndex = -1;

                                    for (int i = 0; i < tagLists.Count; i++)
                                    {
                                        int tagCount = Int32.Parse(tagLists[i][2]);
                                        int tagRSSI = Int32.Parse(tagLists[i][3]);
                                        string tagLabel = RFIDTagInfo.readEPCLabel(tagLists[i][0]);
                                        
                                        if ((readCount < tagCount) &&
                                            (readRSSI < tagRSSI) && (tagRSSI > 60) &&
                                            !((tagInfo.label == tagLabel) &&
                                              (tagInfo.bVerified) &&
                                              ((tagState == readTagStatus.VerifiedFailed) ||
                                               (tagState == readTagStatus.ZeroData))))
                                        {
                                            readCount = tagCount;
                                            readRSSI = tagRSSI;
                                            maxCountIndex = i;
                                        }                                        
                                    }

                                    //can't find good reading tag
                                    if (maxCountIndex < 0)
                                        goto Idle;

                                    strEPC = tagLists[maxCountIndex][0];
                                    string convertLabel = RFIDTagInfo.readEPCLabel(tagLists[maxCountIndex][0]);
                                    if (convertLabel == "")
                                    {//label format not correct
                                        goto Idle;
                                    }
                                    else if ((tagInfo.label == convertLabel) &&
                                             (tagInfo.bVerified))
                                    {//verified
                                        Console.Write("Fount same tag, Current state: " + tagState + " ");
                                        goto Idle;
                                    }
                                    else
                                    {
                                        tagState = readTagStatus.Reading;
                                        Console.Write("Fount new tag, Current state: " + tagState + " ");
                                        tagInfo.label = convertLabel;
                                        tagInfo.bVerified = false;
                                    }

                                    //byte btWordAddr = Convert.ToByte(txtWordAddr.Text);                                         
                                    byte[] btAryPwd = RFIDTagInfo.GetPassword(symmetric.readAccessCode(), 2);
                                    byte[] btAryEpc = RFIDTagInfo.GetData(strEPC.ToUpper(), 2);

                                    if (tagState == readTagStatus.Erasing)
                                    {
                                        Console.WriteLine("Erasing scanning repeating, tag" +
                                            m_curOperateTagBuffer.strAccessEpcMatch + " state: " +
                                            tagState.ToString());

                                        reader.ReadTag(m_curSetting.btReadId, 3, 0, 22, null); // btAryPwd);
                                        Thread.Sleep(rwTagDelay);
                                        break;
                                    }

                                    //symmetric.resetEncrypted();                                        
                                    //tagInfo.HEXLabel = strEPC;
                                    tagState = readTagStatus.ReadReserve;
                                    Console.WriteLine("Scan found tag: " + strEPC + ", RSSI " + readRSSI);

                                    //m_curOperateTagBuffer.strAccessEpcMatch = strEPC; 
                                    reader.SetAccessEpcMatch(m_curSetting.btReadId, 0x00, Convert.ToByte(btAryEpc.Length), btAryEpc);
                                    //Console.WriteLine("Read tag " + strEPC);

                                    m_curOperateTagBuffer.dtTagTable.Clear();
                                    Thread.Sleep(rwTagDelay);

                                    if (tagState == readTagStatus.ReadReserve)
                                        reader.ReadTag(m_curSetting.btReadId, 0, 0, 4, btAryPwd);
                                    else
                                        reader.ReadTag(m_curSetting.btReadId, 3, 0, 22, null); // btAryPwd);

                                    Thread.Sleep(rwTagDelay);
                                    break;
                                }
                            }
                            catch (System.Exception ex)
                            {
                                //MessageBox.Show(ex.Message);
                                Console.WriteLine("Reading tag format not support Exception " + ex.Message);
                                setLEDstaus(LEDStatus.Red);
                                Thread.Sleep(rwTagDelay);
                            }
#endif
                            tagState = readTagStatus.Reading;
                        }
                        else
                        {
                            if(iIdleCount++ > 10)
                            {
                                iIdleCount = 0;
                                resetScanBuffer();
                            }
                            goto Idle;
                        }

                    Idle:
                        setLEDstaus(LEDStatus.Idle);
                        /*
                        //iLedOnCount = 0, iLedOffCount = 0;
                        if (bGreenIdleOn)// && iLedOnCount++ > 3)
                        {
                            iLedOnCount = iLedOffCount = 0;
                            bGreenIdleOn = false;
                            setLEDstaus(LEDStatus.Green);
                        }
                        else if (!bGreenIdleOn)// && iLedOffCount++ > 3)
                        {
                            iLedOnCount = iLedOffCount = 0;
                            bGreenIdleOn = true;
                            setLEDstaus(LEDStatus.GreenOff);
                        }*/
                    }
                    break;

                   
                case 0x00:
                case 0x01:
                    break;
                default:
                    break;
            }
            
        }
    }
}
