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
using System.Diagnostics;

namespace RFIDTag
{    
    public class RFIDMain
    {
        Reader.ReaderMethod reader;
        ReaderSetting m_curSetting = new ReaderSetting();
        InventoryBuffer m_curInventoryBuffer = new InventoryBuffer();
        OperateTagBuffer m_curOperateTagBuffer = new OperateTagBuffer();
        Symmetric_Encrypted symmetric = new Symmetric_Encrypted();

        const short m_nRealRate = 3;
        const short rwTagDelay = 300;
        const string inkFile = "\\AEWA\\APRINT\\pIsNmK_a40670.dat"; //C:\ProgramData
        const string inkAuthFile = "\\inkAuthorization.dat";        //C:\ProgramData
        const string inkLogFolder = "\\RFIDTag";                    //C:\ProgramData
        const string inkLogFile = "\\RFIDTrace.log";                //C:\ProgramData\RFIDTag
        const string RFIDlogFile = "\\log.dat";                     //C:\ProgramData\RFIDTag

        //authorization ink time stamp 1 days allowed
        //also record in log file, and verified it everytime read it
        const int dayAllowed = 1;
        static int firmwareRetry = 0;
        static bool bSendComPort = false;

        int m_nReceiveFlag_3s = 0;
        int m_nReceiveFlag_500ms = 0;
        int m_nTotal = 0;
        bool m_bInventory = false;

        public System.Timers.Timer timerResetLED;
        private static Thread threadInventory;
        List<List<string>> tagLists = new List<List<string>>();

        static LEDStatus resetGreenLED = LEDStatus.GreenOff;
        static LEDStatus resetRedLED = LEDStatus.RedOff;
        static int iComPortStatus = 0;
        string logFolder = "";

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
            //init and add reader callback 
            reader = new Reader.ReaderMethod();
            reader.AnalyCallback = AnalyData;

            try
            {
                //add RFID authorization log
                string path = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                logFolder = path + inkLogFolder;

                SetFolderPermission(logFolder);

                //add trace log here
                Trace.Listeners.Add(new TextWriterTraceListener(logFolder+ inkLogFile, "myListener"));
                Trace.AutoFlush = true;

                Trace.WriteLine("Path: " + path);
                RFIDTagInfo.loadFilePath(path, inkLogFolder + RFIDlogFile, inkFile, inkAuthFile);       
            }
            catch (Exception exp) { Trace.WriteLine("Got Exception " + exp.Message); }

            //start reader scan tag thread
            StartThread();
        }

        public void RFIDStop()
        {
            Trace.Flush();
            Trace.Close();
            Trace.Listeners.Clear();
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
            if (m_nReceiveFlag_500ms++ >= 5)
            {
                m_nReceiveFlag_500ms = 0;
                m_nReceiveFlag_3s++;

                if(bSendComPort == true)
                {//no respond from Reader, for 2nd and more unplug
                    firmwareRetry++;
                }
                else
                {
                    firmwareRetry = 0;
                    bSendComPort = true;
                }

                if (iComPortStatus != 1)
                {//com port connect failed
                    checkComPort();
                }
                else
                {
                    if ((reader != null && m_curSetting.btReadId == 0xFF) ||
                        (firmwareRetry > 4))
                    {//for the first unplug
                        reader.GetFirmwareVersion(m_curSetting.btReadId);
                        Thread.Sleep(rwTagDelay);

                        if (firmwareRetry++ > 4)
                        {
                            firmwareRetry = 0;
                            iComPortStatus = -1;
                            checkComPort();
                        }                        
                        return;
                    }
                    RunLoopInventroy();
                }
            }

            if (m_nReceiveFlag_3s > 3)
            {//C:\\ProgramData\InkAuth...
                if (File.Exists(RFIDTagInfo.readinkAuthFilePath()))
                {
                    //Trace.WriteLine("File found readinkAuthFile");
                    checkAuthFile();                    
                }
                m_nReceiveFlag_3s = 0;
                addLog();
            }
        }

        void addLog()
        {
            //check and updata log file
            FileInfo fi = new FileInfo(logFolder + inkLogFile);
            if ((File.Exists(logFolder + inkLogFile)) && (fi.Length / 1024 / 1024 > 5))
            {
                string[] fileEntries = Directory.GetFiles(logFolder);
                DateTime lastModified = new DateTime();
                string lastModifiedFile = "";
                int index = -1;
                foreach (string fileName in fileEntries)
                {
                    if (fileName.Contains("RFIDTrace") &&
                        int.TryParse(fileName.Substring(fileName.Length - 5, 1), out index))
                    {
                        if (lastModified < File.GetLastWriteTime(fileName))
                        {
                            lastModified = File.GetLastWriteTime(fileName);
                            lastModifiedFile = fileName;
                            fi = new FileInfo(fileName);
                            if (fi.Length / 1024 / 1024 > 5)
                            {
                                if (index >= 6)
                                {
                                    index = 0;
                                }
                                else if (index >= 0 && index < 6)
                                {
                                    index++;
                                }
                            }
                        }
                    }
                }
                lastModifiedFile = logFolder + "\\RFIDTrace" + index.ToString() + ".log";
                Trace.Flush();

                if (File.Exists(lastModifiedFile))
                    File.Delete(lastModifiedFile);

                File.Copy(logFolder + inkLogFile, lastModifiedFile);
                Trace.Close();
                Trace.Listeners.Clear();
                File.Delete(logFolder + inkLogFile);
                Trace.Listeners.Add(new TextWriterTraceListener(logFolder + inkLogFile, "myListener"));
                Trace.AutoFlush = true;
            }
        }

        public void checkComPort()
        {
            //load serial port
            string[] serialPort = SerialPort.GetPortNames();
            int i = serialPort.Length - 1;
            do
            {               
                if (iComPortStatus != 1)
                {
                    string comPort = serialPort[i];
                    checkPort(comPort);
                    Thread.Sleep(rwTagDelay*3);
                }
            }
            while ((iComPortStatus != 1) && (i-- > 0));

            if(iComPortStatus == 1)
            {
                DateTime dateTimeNow = DateTime.Now;
                Trace.WriteLine("Message start: " + dateTimeNow.ToString());
                Trace.WriteLine("Com port found, Load setting");
                //Trace.WriteLine("Com port found, Load setting");
                loadSetting();
            }
            else
            {
                Trace.WriteLine("Error, can't connect to any Com Port");
            }            
        }

        public void checkAuthFile()
        {//read from encrypted log file and compare
            string strAuthData = "";
            try
            {
                using (StreamReader sr = File.OpenText(RFIDTagInfo.readinkAuthFilePath()))
                {
                    string line = sr.ReadLine();
                    strAuthData = symmetric.DecryptData(Convert.FromBase64String(line));
                    sr.Close();
                }
                
                if (strAuthData != "")
                {
                    string[] authDataArry = strAuthData.Split(',');
                    if (RFIDTagInfo.checkAuthDate(authDataArry[0], dayAllowed) == 1)
                    {
                        if (File.Exists(RFIDTagInfo.readLogFilePath()))
                        {
                            FileInfo fi = new FileInfo(RFIDTagInfo.readLogFilePath());
                            if (fi.Length != 0)
                            {
                                using (StreamReader sr = File.OpenText(RFIDTagInfo.readLogFilePath()))
                                {
                                    string line;
                                    while ((line = sr.ReadLine()) != null)
                                    {
                                        if (line == "")
                                            continue;

                                        string plaintext = symmetric.DecryptData(System.Convert.FromBase64String(line));                                   
                                        string[] logArry = plaintext.Split(',');

                                        if (string.Compare(authDataArry[1], logArry[1]) == 0)
                                        {
                                            Trace.WriteLine("*** Tag existed ***");
                                            setLEDstaus(LEDStatus.Red);
                                            //File.Delete(RFIDTagInfo.readinkAuthFilePath());                                           
                                            return;
                                        }
                                    }
                                    sr.Close();
                                }
                            }
                        }                        
                        
                        string encryptData = symmetric.Encrypt(
                                                RFIDTagInfo.getLogData(false, authDataArry[1]));

                        UInt32 inkVolumeFromFile = Convert.ToUInt32(authDataArry[2]);
                        UInt32 dongleIDFromFile = Convert.ToUInt32(authDataArry[3]);
                        if (RFIDTagInfo.addVolumeToFile(inkVolumeFromFile, dongleIDFromFile, true))
                        {
                            if (RFIDTagInfo.addLog(encryptData))
                            {
                                Trace.WriteLine("*** Add Ink from file ***");
                                File.Delete(RFIDTagInfo.readinkAuthFilePath());
                                if (iComPortStatus == 1)
                                {
                                    setLEDstaus(LEDStatus.RedOff);
                                    setLEDstaus(LEDStatus.Green);
                                    setLEDstaus(LEDStatus.Green);
                                }
                            }
                        }                        
                        
                    }
                }
            }
            catch (Exception exp) { 
                Trace.WriteLine("Get Exception " + exp.Message);
            }
        }

        public void checkPort(string comPort)
        {
            string strException = string.Empty;
          
            int nRet = reader.OpenCom(comPort, Convert.ToInt32("115200"), out strException);
            if (nRet != 0)
            {
                Trace.WriteLine("Connection failed, failure cause: " + strException);
                return;
            }
            else
            {
                Trace.WriteLine("Connect " + comPort + "@" + "115200");
            }
            Thread.Sleep(rwTagDelay);
            reader.resetCom();
            Thread.Sleep(rwTagDelay);
                    
            reader.GetFirmwareVersion(m_curSetting.btReadId);
            Thread.Sleep(rwTagDelay*3);
        }

        public static void SetFolderPermission(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                System.Security.AccessControl.DirectorySecurity dirSecurity =
                            new System.Security.AccessControl.DirectorySecurity();
                dirSecurity.AddAccessRule(
                    new System.Security.AccessControl.FileSystemAccessRule("Everyone",
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                    System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow)
                );
                // Create the new folder with the custom ACL.
                Directory.CreateDirectory(folderPath, dirSecurity);
            }
            DirectoryInfo info = new DirectoryInfo(folderPath);
            info.Attributes = FileAttributes.Hidden;
        }

        public void loadSetting()
        { //R2000UartDemo_Load //C:\ProgramData
            //load Access Code
            byte[] accessCode = Properties.Resources.AccessCode;
            string strCode = RFIDTagInfo.ASCIIToHex(Encoding.ASCII.GetString(accessCode)).ToUpper();
            symmetric.loadAccessCode(strCode);
            //Trace.WriteLine("Read Access code " + strCode);

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
                //Trace.WriteLine(strCmd);
                Trace.WriteLine("Get firmware version " + 
                                  m_curSetting.btMajor.ToString() + "." +
                                  m_curSetting.btMinor.ToString());
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
            Trace.WriteLine(strLog);
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
                    //Trace.WriteLine(strCmd);
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
            Trace.WriteLine(strLog);
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
                        if (m_curInventoryBuffer.bLoopCustomizedSession)
                        {
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
                }
            }
            else if (m_curInventoryBuffer.bLoopInventory)
            {//enter here
                m_curInventoryBuffer.nIndexAntenna = 0;
                m_curInventoryBuffer.nCommond = 0;
            }            
            Thread.Sleep(10);
        }  

        void AnalyData(Reader.MessageTran msgTran)
        {
            m_nReceiveFlag_500ms = 0;
            bSendComPort = false;

            if (msgTran.PacketType != 0xA0)
            {
                return;
            }
            switch (msgTran.Cmd)
            {
                case 0x72:
                    ProcessGetFirmwareVersion(msgTran);
                    break;

                case 0x61:
                    ProcessWriteGpioValue(msgTran);
                    break;
                case 0x81:
                    {
                        //byte btMemBank = 3; // 0
                        //byte btWordAddr = 0; // 0
                        //byte btWordCnt = 30; // 4
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
                                        reader.ReadTag(m_curSetting.btReadId, 3, 0, 30, null); //btAryPwd);
                                        break;
                                }                                                         
                                //Trace.WriteLine("Read Tag retry (" + readTagRetry + "), state " + 
                                //                  tagState.ToString());
                                Thread.Sleep(rwTagDelay); //set to 20 will delay the respond
                                break;
                            }
                            else
                            {                                 
                                Trace.WriteLine("Read tag failed" + ", state " +
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
                                reader.ReadTag(m_curSetting.btReadId, 3, 0, 30, null); //btAryPwd);
                                Thread.Sleep(rwTagDelay);
                                //Trace.WriteLine("Read Tag ok" +
                                //                  ", state " + tagState.ToString());
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

                                //Trace.WriteLine("Write Tag retry " + writeTagRetry +
                                //    ", state " + tagState.ToString());
                                Thread.Sleep(rwTagDelay);
                                break;
                            }
                            else
                            {                                
                                Trace.WriteLine("Write tag failed" +
                                    ", state " + tagState.ToString());
                                writeTagRetry = 0;                                
                                setLEDstaus(LEDStatus.Red);                                                              
                            }                            
                        }
                        else
                        {
                            //Trace.WriteLine("Write Tag successful" +
                            //    ", state " + tagState.ToString());
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
                    Trace.Write("Retry write GPIO " + writeCnt +
                                  " rate " + writeFailed + " (" + writeSuccessed + ")");                   
                }
                Thread.Sleep(50);
            } while ((bWriteResult != 0) && writeCnt < writeMAXCnt);           
        }

        private void setLEDstaus(LEDStatus ledStatus)
        {//GPIO 4 green, GPIO 3 red
            if(m_curSetting.btReadId == 0xFF)
            {//no com port commenction, return
                return;
            }

            switch (ledStatus)
            {
                case LEDStatus.GreenOff:
                    {
                        WriteLEDGPIO(m_curSetting.btReadId, 0x04, 0);
                        m_curSetting.btGpio4Value = 0;
                        //Trace.WriteLine("LED Green OFF ");
                        resetGreenLED = ledStatus;
                    }
                    return;
                case LEDStatus.RedOff:
                    {
                        WriteLEDGPIO(m_curSetting.btReadId, 0x03, 0);
                        m_curSetting.btGpio3Value = 0;
                        //Trace.WriteLine("LED Red OFF, ");
                        resetRedLED = ledStatus;
                    }
                    return;
                case LEDStatus.Off:
                    {
                        //if (resetGreenLED == LEDStatus.Green)
                        {
                            WriteLEDGPIO(m_curSetting.btReadId, 0x04, 0);
                            m_curSetting.btGpio4Value = 0;
                            //Trace.Write("LED Green OFF ");
                            resetGreenLED = LEDStatus.Off;
                            Thread.Sleep(rwTagDelay);
                        }
                        //if (resetRedLED == LEDStatus.Red)
                        {
                            WriteLEDGPIO(m_curSetting.btReadId, 0x03, 0);
                            m_curSetting.btGpio3Value = 0;
                            //Trace.WriteLine("LED Red OFF, ");
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
                            //Trace.Write("LED Green Off, ");
                        }
                        WriteLEDGPIO(m_curSetting.btReadId, 0x03, 1);
                        m_curSetting.btGpio3Value = 1;
                        Trace.Write("LED Red ON, ");
                        resetRedLED = ledStatus;
                    }
                    break;
                case LEDStatus.Green:
                    {
                        WriteLEDGPIO(m_curSetting.btReadId, 0x04, 1);
                        m_curSetting.btGpio4Value = 1;
                        Trace.WriteLine("LED Green ON ");
                        resetGreenLED = ledStatus;
                    }
                    break;
                case LEDStatus.Idle:
                    {
                        //if (resetRedLED == LEDStatus.Red)
                        {
                            WriteLEDGPIO(m_curSetting.btReadId, 0x03, 0);
                            m_curSetting.btGpio3Value = 0;
                            //Trace.Write("LED Red OFF, ");
                            resetRedLED = LEDStatus.RedOff;
                            Thread.Sleep(rwTagDelay);
                        }

                        m_curSetting.btGpio4Value = 1;
                        WriteLEDGPIO(m_curSetting.btReadId, 0x04, 1);                       
                        //Trace.Write("LED Green ON, ");
                        Thread.Sleep(rwTagDelay);

                        m_curSetting.btGpio4Value = 0;
                        resetGreenLED = LEDStatus.GreenOff;
                        WriteLEDGPIO(m_curSetting.btReadId, 0x04, 0);
                        //Trace.WriteLine("LED Green OFF ");
                    }
                    break;
            }            
            Thread.Sleep(rwTagDelay);
        }

        private void verifyTag(string data)
        {
            //Trace.WriteLine("Load Key " + symmetric.readKey());
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
                    Trace.WriteLine("Verified Erase data successful, " + data);                    
                }
                else
                {//re-erase tag                    
                    Trace.WriteLine("Verified Erase data failed, id " + tagInfo.label);
                }
                return;
            }

            string decryptMsg = symmetric.DecryptFromHEX(tagInfo.tagReserve, tagInfo.tagData);
            if (decryptMsg != "")
            {
                //Trace.WriteLine("Decrypt data " + decryptMsg + ", Size " + decryptMsg.Length);
                tagInfo.tagDecryptedInfo = decryptMsg;
                if (RFIDTagInfo.verifyData(decryptMsg, true, false))
                {
                    tagState = readTagStatus.VerifiedSuccessful;
                    //Trace.WriteLine("Verified data successful, " + decryptMsg);
                }
                else
                {
                    if(tagState == readTagStatus.ReadReserveOK)
                        tagState = readTagStatus.VerifiedFailed;

                    Trace.WriteLine("Verified data failed, " + decryptMsg + 
                                      ", state " + tagState.ToString());
                }
            }
            else if (tagState != readTagStatus.ZeroData)
            {
                tagState = readTagStatus.VerifiedFailed;
                Trace.WriteLine("Read data failed, state " + tagState.ToString());
            }
        }

        private string lookupTable(string label, string data)
        {
            ulong labelNum = 0;
            string data1 = data;

            RFIDTagInfo.readEPCLabel(label, out labelNum);

            if (labelNum < 99999999999999)
            {
                if (data.Length > 120)
                {
                    data1 = data.Substring(0, 120);
                }
            }
            return data1;
        }

        bool ProcessReadTag(Reader.MessageTran msgTran)
        {
            string strCmd = "Read Tag";
            string strErrorCode = string.Empty;

            try
            {
                //Trace.WriteLine("Read Tag, AryData.Length " + msgTran.AryData.Length, 0);
                if (msgTran.AryData.Length == 1)
                {
                    strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[0]);

                    if (tagState != readTagStatus.ReadReserve)
                    {
                        Trace.WriteLine(strCmd + " Failure, read tag failure cause1: " + strErrorCode);
                    }
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
                    //read data section
                    try
                    {//find error in encrypted function, got 1 more bytes
                        strData = lookupTable(strEPC, strData);

                        verifyTag(strData);
                        Trace.WriteLine(" State: " + tagState.ToString());
                        switch (tagState)
                        {
                            case readTagStatus.EraseConfirmed:
                                {
                                    //1. Erase verified success tag
                                    //2. add ink volume
                                    if (RFIDTagInfo.addLog(symmetric.Encrypt(RFIDTagInfo.getLogData(true, ""))))
                                    {
                                        setLEDstaus(LEDStatus.RedOff);
                                        setLEDstaus(LEDStatus.Green); //green on
                                        setLEDstaus(LEDStatus.Green); //green on
                                        setLEDstaus(LEDStatus.Green); //green on
                                        tagInfo.bVerified = true;
                                        tagState = readTagStatus.ZeroData;
                                    }
                                    break; // return true;
                                }

                            case readTagStatus.VerifiedSuccessful:
                                {
#if READ2ERASE                                                                      
                                    string[] tempList = tagInfo.tagDecryptedInfo.Split(RFIDTagInfo.serialSep);
                                    UInt32 intToneVolume = Convert.ToUInt32(tempList[1].Substring(4, 4));
                                    if(RFIDTagInfo.addVolumeToFile(intToneVolume, 0, false))
                                    {
                                        tagState = readTagStatus.Erasing;
                                        writeZeroTag(3, 0, 30);
                                        Trace.WriteLine("Erasing data ");
                                        Thread.Sleep(rwTagDelay);
                                    }
                                    else
                                    {
                                        setLEDstaus(LEDStatus.Idle);
                                        return false;
                                    }
#else
                                    string[] tempList = tagInfo.tagDecryptedInfo.Split(RFIDTagInfo.serialSep);
                                    UInt32 intToneVolume = Convert.ToUInt32(tempList[1].Substring(4, 4));
                                    if (RFIDTagInfo.addVolumeToFile(intToneVolume, 0, false))
                                    {
                                        if (RFIDTagInfo.addLog(symmetric.Encrypt(RFIDTagInfo.getLogData(true, ""))))
                                        {
                                            setLEDstaus(LEDStatus.Green); //green on
                                            tagInfo.bVerified = true;
                                            tagState = readTagStatus.ZeroData;
                                        }
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
                        Trace.WriteLine(strCmd + " tag " + strEPC 
                                            + " got error " + exp.Message +
                                            ", state " + tagState.ToString());
                        return false;
                    }
                }                  
                          
            }
            catch (Exception exp)
            {
                //setLEDstaus(LEDStatus.Red); //red on      
                Trace.WriteLine(strCmd + " got error " + exp.Message +
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
                Trace.WriteLine(strCmd + " Failure, failure cause1: " + strErrorCode);
                return false;
            }
            else
            {
                int nLen = msgTran.AryData.Length;
                int nEpcLen = Convert.ToInt32(msgTran.AryData[2]) - 4;

                if (msgTran.AryData[nLen - 3] != 0x10)
                {
                    strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[nLen - 3]);                 
                    Trace.WriteLine(strCmd + " tag " +
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
                    reader.ReadTag(m_curSetting.btReadId, 3, 0, 30, btAryPwd);
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
                Trace.WriteLine(strLog);

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
                //Trace.WriteLine(strCmd);
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
                                        //Trace.WriteLine("Add tag: " + row[2].ToString());
                                    }
                                }
                                nIndex++;
                            }
#if SCAN2READ
                            try
                            {
                                if (tagLists.Count == 0)
                                {
                                    Trace.WriteLine("Can't find tag, idle");
                                    goto Idle;
                                }
                                else //if (tagLists.Count > 0)
                                {//scan a tag, get the best <70dbm RSSI, then read the tag
                                 //verify is same as label format
                                    int readCount = 0;
                                    int readRSSI = 60;
                                    int maxCountIndex = -1;
                                    ulong labelNum = 0;
                                    for (int i = 0; i < tagLists.Count; i++)
                                    {
                                        int tagCount = Int32.Parse(tagLists[i][2]);
                                        int tagRSSI = Int32.Parse(tagLists[i][3]);
                                        string tagLabel = RFIDTagInfo.readEPCLabel(tagLists[i][0], out labelNum);
                                        
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
                                    string convertLabel = RFIDTagInfo.readEPCLabel(tagLists[maxCountIndex][0], out labelNum);
                                    if (convertLabel == "" || labelNum == 0)
                                    {//label format not correct
                                        goto Idle;
                                    }
                                    else if ((tagInfo.label == convertLabel) &&
                                             (tagInfo.bVerified || tagState == readTagStatus.Erasing))
                                    {//verified
                                        //Trace.Write("Fount same tag, Current state: " + tagState + " ");
                                        goto Idle;
                                    }
                                    else if(tagInfo.label != convertLabel)
                                    {
                                        tagState = readTagStatus.Reading;
                                        //Trace.Write("Found new tag, Current state: " + tagState + " ");
                                        tagInfo.label = convertLabel;
                                        tagInfo.bVerified = false;
                                    }

                                    //byte btWordAddr = Convert.ToByte(txtWordAddr.Text);                                         
                                    byte[] btAryPwd = RFIDTagInfo.GetPassword(symmetric.readAccessCode(), 2);
                                    byte[] btAryEpc = RFIDTagInfo.GetData(strEPC.ToUpper(), 2);

                                    if (tagState == readTagStatus.Erasing)
                                    {
                                        //Trace.WriteLine("Erasing scanning repeating, tag" +
                                        //    m_curOperateTagBuffer.strAccessEpcMatch + " state: " +
                                        //    tagState.ToString());

                                        reader.ReadTag(m_curSetting.btReadId, 3, 0, 30, null); // btAryPwd);
                                        Thread.Sleep(rwTagDelay);
                                        break;
                                    }

                                    //symmetric.resetEncrypted();                                        
                                    //tagInfo.HEXLabel = strEPC;                                  
                                    tagState = readTagStatus.ReadReserve;
                                    Trace.WriteLine("Scan found tag: " + strEPC + ", RSSI " + readRSSI);

                                    //m_curOperateTagBuffer.strAccessEpcMatch = strEPC; 
                                    reader.SetAccessEpcMatch(m_curSetting.btReadId, 0x00, Convert.ToByte(btAryEpc.Length), btAryEpc);
                                    //Trace.WriteLine("Read tag " + strEPC);

                                    m_curOperateTagBuffer.dtTagTable.Clear();
                                    Thread.Sleep(rwTagDelay);

                                    if (tagState == readTagStatus.ReadReserve)
                                        reader.ReadTag(m_curSetting.btReadId, 0, 0, 4, btAryPwd);
                                    else
                                        reader.ReadTag(m_curSetting.btReadId, 3, 0, 30, null); // btAryPwd);

                                    Thread.Sleep(rwTagDelay);
                                    break;
                                }
                            }
                            catch (System.Exception ex)
                            {
                                //MessageBox.Show(ex.Message);
                                Trace.WriteLine("Reading tag format not support Exception " + ex.Message);
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
