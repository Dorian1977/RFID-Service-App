//#defin#elsee LOG_ENCRYPTED
//#define WRITE_RESERVE
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
    public class RFIDReaderSetting
    {
#if TRUE
        public int nStartFrequency = 13850000; //Start Freq: kHz
        public byte nFrequencyInterval = 5;    //5, Freq Sapce: per 10KHz
        public byte btChannelQuantity = 5;     //5, Quentity
#else
        public int nStartFrequency = 902000;
        public byte nFrequencyInterval = 42;//5;
        public byte btChannelQuantity = 42;//30;
#endif
        public short threadSleep = 100; //50 ms, got 3.6 ~ 5s, 100ms got 1.05 ~ 2.3s
#if DEBUG
        public short m_nRealRate = 10; //5;
        public short rwTagDelay = 50; //50ms, read failed(1/5). 30ms, read failed (2/3). 20ms, read failed (3/5)
        public short LEDDelay = 300;//300ms (3.5s, 4.7s, 4.6s); 200ms (1.4s, 2.39, 3.3), 100ms read failed (5 times)
#else
        public short m_nRealRate = 5; //5;
        public short rwTagDelay = 30; //50ms, read failed(1/5). 30ms, read failed (2/3). 20ms, read failed (3/5)
        public short LEDDelay = 100;//300ms (3.5s, 4.7s, 4.6s); 200ms (1.4s, 2.39, 3.3), 100ms read failed (5 times)
#endif
        public byte[] OutputPower = { 18 }; //26-18, 10
        public byte btBeeperMode = 0x00;    //BeeperModeInventory 0x01, quiet 0x00        
        public string readComPort = "115200"; //"38400" is slow;
        public RFIDReaderSetting() { }
    }

    public class RFIDMain
    {
        Reader.ReaderMethod reader;
        ReaderSetting m_curSetting = new ReaderSetting();
        RFIDReaderSetting rfidSetting = new RFIDReaderSetting();
        InventoryBuffer m_curInventoryBuffer = new InventoryBuffer(); 
        OperateTagBuffer m_curOperateTagBuffer = new OperateTagBuffer();
        Symmetric_Encrypted symmetric = new Symmetric_Encrypted();

        const string inkFile = "\\AEWA\\APRINT\\pIsNmK_a40670.dat"; //C:\ProgramData
        const string inkAuthFile = "\\inkAuthorization.dat";        //C:\ProgramData
        const string inkLogFolder = "\\RFIDTag";                    //C:\ProgramData
        const string inkLogFile = "\\RFIDTrace.log";                //C:\ProgramData\RFIDTag
        const string RFIDlogFile = "\\log.dat";                     //C:\ProgramData\RFIDTag

        //authorization ink time stamp 1 days allowed
        //also record in log file, and verified it everytime read it
        const int dayAllowed = 1;
        int firmwareRetry = 0;
        bool bSendComPort = false;

        int m_nReceiveFlag_3s = 0;
        int m_nReceiveFlag_300ms = 0;
        int m_nTotal = 0;
        bool m_bInventory = false;

        public System.Timers.Timer timerResetLED;
        private static Thread threadInventory;
        List<RFIDTagData> tagLists = new List<RFIDTagData>();
        
        static int iComPortStatus = 0;
        string logFolder = "";

        private const int rwTagRetryMAX = 5;
        int readTagRetry = 0;
        int writeTagRetry = 0;
               
        DateTime startReading = DateTime.Now;
        enum LEDStatus
        {
            Off,
            Green,
            GreenOff,
            Red,
            RedOff,
            BlinkingGreen,
            BlinkingRed
        }

        readTagStatus tagState = readTagStatus.Reading;
        enum readTagStatus
        {
            Reading,
            ReadReserve,
            ReadReserveOK,
            ReadReserveFailed,
            VerifiedSuccessful,
            VerifiedFailed,
            Erasing,
            Erased,
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
                        Thread.Sleep(rfidSetting.threadSleep); 
                    }
                    catch (ThreadInterruptedException) { }
                }
            });
            threadInventory.Start();

            timerResetLED = new System.Timers.Timer(3000);
            timerResetLED.Elapsed += OnTimerResetLED;
            timerResetLED.AutoReset = false;
            timerResetLED.Enabled = false;
        }

        void OnTimerResetLED(object sender, ElapsedEventArgs e)
        {
            Trace.WriteLine("Timer hold LED");
            setLEDstaus(LEDStatus.Off);
            setLEDstaus(LEDStatus.Off);
        }

        void scanInventory()
        {
            if (m_nReceiveFlag_300ms++ >= 3)
            {
                m_nReceiveFlag_300ms = 0;
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
                        Thread.Sleep(rfidSetting.rwTagDelay);

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

            if (m_nReceiveFlag_3s > 300)
            {//C:\\ProgramData\InkAuth...
                if (File.Exists(RFIDTagInfo.readinkAuthFilePath()))
                {
                    Trace.WriteLine("File found ink Auth. File");
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

        private int messageCount = 0;
        public void checkComPort()
        {
            //load serial port
            string[] serialPort = SerialPort.GetPortNames();
            int i = serialPort.Length - 1;
            do
            {               
                if (iComPortStatus != 1)
                {
                    checkPort(serialPort[i]);
                    Thread.Sleep(rfidSetting.rwTagDelay *3);
                }
            }
            while ((iComPortStatus != 1) && (i-- > 0));

            if(iComPortStatus == 1)
            {
                DateTime dateTimeNow = DateTime.Now;
                Trace.WriteLine("Message start: " + dateTimeNow.ToString());
                Trace.WriteLine("Com port found, Load setting");
                //Trace.WriteLine("Com port found, Load setting");
#if DEBUG
                Trace.WriteLine("*** Mode = Debug ***");
#else
                Trace.WriteLine("*** Mode = Release ***"); 
#endif
                loadSetting();
            }
            else
            {
                if (messageCount++ < 5)
                {
                    Trace.WriteLine("Error, can't connect to any Com Port");
                }
                else
                {
                    messageCount = 5;
                }
            }            
        }
        
        public void checkPort(string comPort)
        {
            string strException = string.Empty;          
            int nRet = reader.OpenCom(comPort, Convert.ToInt32(rfidSetting.readComPort), out strException);
            if (nRet != 0)
            {
                Trace.WriteLine("Connection failed, failure cause: " + strException);
                return;
            }
            else
            {
                Trace.WriteLine("Connect " + comPort + "@" + rfidSetting.readComPort.ToString());
            }
            Thread.Sleep(rfidSetting.rwTagDelay);
            reader.resetCom();
            Thread.Sleep(rfidSetting.rwTagDelay);
                    
            reader.GetFirmwareVersion(m_curSetting.btReadId);
            Thread.Sleep(rfidSetting.rwTagDelay *3);
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
                                            LEDTimerStart(true);
                                            //File.Delete(RFIDTagInfo.readinkAuthFilePath());                                           
                                            return;
                                        }
                                    }
                                    sr.Close();
                                }
                            }
                        }

                        UInt32 inkVolumeFromFile = Convert.ToUInt32(authDataArry[2]);
                        UInt32 dongleIDFromFile = Convert.ToUInt32(authDataArry[3]);
                        if (RFIDTagInfo.addVolumeToFile(inkVolumeFromFile, dongleIDFromFile, true))
                        {
#if LOG_ENCRYPTED
                            string encryptData = symmetric.Encrypt(
                                                    RFIDTagInfo.getLogData(false, authDataArry[1], tagInfo));
                            if (RFIDTagInfo.addLog(encryptData))
#else
                            if (RFIDTagInfo.addLog(RFIDTagInfo.getLogData(false, authDataArry[1], null)))
#endif
                            {
                                Trace.WriteLine("*** Add Ink from file ***");
                                File.Delete(RFIDTagInfo.readinkAuthFilePath());
                                if (iComPortStatus == 1)
                                {
                                    setLEDstaus(LEDStatus.Green);
                                    setLEDstaus(LEDStatus.Green);
                                    LEDTimerStart(true);
                                }
                            }
                        }

                    }
                }
            }
            catch (Exception exp)
            {
                Trace.WriteLine("Get Exception " + exp.Message);
            }
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
        { //C:\ProgramData\RFIDTag\
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

            Trace.WriteLine("Set output power " + rfidSetting.OutputPower[0].ToString());
            reader.SetOutputPower(m_curSetting.btReadId, rfidSetting.OutputPower);
            Thread.Sleep(rfidSetting.rwTagDelay);


            reader.SetUserDefineFrequency(  m_curSetting.btReadId, 
                                            rfidSetting.nStartFrequency, 
                                            rfidSetting.nFrequencyInterval, 
                                            rfidSetting.btChannelQuantity);
            m_curSetting.btRegion = 4;
            m_curSetting.nUserDefineStartFrequency = rfidSetting.nStartFrequency;
            m_curSetting.btUserDefineFrequencyInterval = rfidSetting.nFrequencyInterval;
            m_curSetting.btUserDefineChannelQuantity = rfidSetting.btChannelQuantity;
            Thread.Sleep(rfidSetting.rwTagDelay);

            reader.GetFrequencyRegion(m_curSetting.btReadId);
            Thread.Sleep(rfidSetting.rwTagDelay);

            reader.SetWorkAntenna(m_curSetting.btReadId, 0x00);
            Thread.Sleep(rfidSetting.rwTagDelay);

            m_curSetting.btWorkAntenna = 0x00;
            tagState = readTagStatus.Reading;

            reader.GetOutputPowerFour(m_curSetting.btReadId);
            Thread.Sleep(rfidSetting.rwTagDelay);
                       
            reader.SetBeeperMode(m_curSetting.btReadId, rfidSetting.btBeeperMode);
            m_curSetting.btBeeperMode = rfidSetting.btBeeperMode;
            Thread.Sleep(rfidSetting.rwTagDelay);          
        }

        public void LEDTimerStart(bool bEnableTimer)
        {
            //tagLists.Clear();
            m_nTotal = 0;
            m_curInventoryBuffer.ClearInventoryResult(); // reset timer

            if (bEnableTimer)
            {
                timerResetLED.Enabled = true;
                timerResetLED.Start();
            }
            else
            {
                timerResetLED.Enabled = false;
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

        private void ProcessSetUartBaudrate(Reader.MessageTran msgTran)
        {
            string strCmd = "Set Baudrate";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                if (msgTran.AryData[0] == 0x10)
                {
                    m_curSetting.btReadId = msgTran.ReadId;
                    //WriteLog(lrtxtLog, strCmd, 0);
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
            Trace.WriteLine(strLog);
        }
        
        private void ProcessSetOutputPower(Reader.MessageTran msgTran)
        {
            string strCmd = "Set RF Output Power";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                if (msgTran.AryData[0] == 0x10)
                {
                    m_curSetting.btReadId = msgTran.ReadId;
                    Trace.WriteLine(strCmd);

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
            Trace.WriteLine(strLog);
        }

        private void ProcessSetBeeperMode(Reader.MessageTran msgTran)
        {
            string strCmd = "Set reader's buzzer hehavior";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                if (msgTran.AryData[0] == 0x10)
                {
                    m_curSetting.btReadId = msgTran.ReadId;
                    //WriteLog(lrtxtLog, strCmd, 0);
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
            Trace.WriteLine(strLog);
        }

        private void ProcessGetOutputPower(Reader.MessageTran msgTran)
        {
            string strCmd = "Get RF Output Power";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                m_curSetting.btReadId = msgTran.ReadId;
                m_curSetting.btOutputPower = msgTran.AryData[0];

                if(msgTran.AryData[0] != rfidSetting.OutputPower[0])
                {
                    reader.SetOutputPower(m_curSetting.btReadId, rfidSetting.OutputPower);
                }
                return;
            }

            string strLog = strCmd + "Failure, failure cause: " + strErrorCode;
            Trace.WriteLine(strLog);
        }
        
        private void ProcessSetFrequencyRegion(Reader.MessageTran msgTran)
        {
            string strCmd = "Set RF frequency spectrum";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                if (msgTran.AryData[0] == 0x10)
                {
                    m_curSetting.btReadId = msgTran.ReadId;                   
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
            Trace.WriteLine(strLog);
        }

        private void ProcessGetFrequencyRegion(Reader.MessageTran msgTran)
        {
            string strCmd = "Query RF frequency spectrum    ";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 3)
            {
                m_curSetting.btReadId = msgTran.ReadId;
                m_curSetting.btRegion = msgTran.AryData[0];
                m_curSetting.btFrequencyStart = msgTran.AryData[1];
                m_curSetting.btFrequencyEnd = msgTran.AryData[2];                
                Trace.WriteLine(strCmd);
                return;
            }
            else if (msgTran.AryData.Length == 6)
            {
                m_curSetting.btReadId = msgTran.ReadId;
                m_curSetting.btRegion = msgTran.AryData[0];
                m_curSetting.btUserDefineFrequencyInterval = msgTran.AryData[1];
                m_curSetting.btUserDefineChannelQuantity = msgTran.AryData[2];
                m_curSetting.nUserDefineStartFrequency = msgTran.AryData[3] * 256 * 256 + msgTran.AryData[4] * 256 + msgTran.AryData[5];
                Trace.WriteLine(strCmd);
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

        private void ProcessGetAccessEpcMatch(Reader.MessageTran msgTran)
        {
            string strCmd = "Get selected tag";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                if (msgTran.AryData[0] == 0x01)
                {
                    Trace.WriteLine("Unselected Tag");
                    return;
                }
                else
                {
                    strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[0]);
                }
            }
            else
            {
                if (msgTran.AryData[0] == 0x00)
                {
                    m_curOperateTagBuffer.strAccessEpcMatch = CCommondMethod.ByteArrayToString(msgTran.AryData, 2, Convert.ToInt32(msgTran.AryData[1]));
                    //RefreshOpTag(0x86);
                    //Trace.WriteLine(strCmd, 0);
                    return;
                }
                else
                {
                    strErrorCode = "Unknown Error";
                }
            }

            string strLog = strCmd + "Failure, failure cause: " + strErrorCode;

            Trace.WriteLine(strLog);
        }

        private void ProcessSetAccessEpcMatch(Reader.MessageTran msgTran)
        {
            string strCmd = "Select/Deselect Tag";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                if (msgTran.AryData[0] == 0x10)
                {
                    //WriteLog(lrtxtLog, strCmd, 0);
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
            Trace.WriteLine(strLog);
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
                            //reader.CustomizedInventoryV2(m_curSetting.btReadId, m_curInventoryBuffer.CustomizeSessionParameters.ToArray());
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
            Thread.Sleep(rfidSetting.rwTagDelay);
        }  

        void AnalyData(Reader.MessageTran msgTran)
        {
            m_nReceiveFlag_300ms = 0;
            bSendComPort = false;

            if (msgTran.PacketType != 0xA0)
            {
                return;
            }
            switch (msgTran.Cmd)
            {
                case 0x71:
                    ProcessSetUartBaudrate(msgTran);
                    break;
                case 0x72:
                    ProcessGetFirmwareVersion(msgTran);
                    break;
                case 0x76:
                    ProcessSetOutputPower(msgTran);
                    break;
                case 0x97:
                case 0x77:
                    ProcessGetOutputPower(msgTran);
                    break;
                case 0x78:
                    ProcessSetFrequencyRegion(msgTran);
                    break;
                case 0x79:
                    ProcessGetFrequencyRegion(msgTran);
                    break;
                case 0x7A:
                    ProcessSetBeeperMode(msgTran);
                    break;
                case 0x61:
                    ProcessWriteGpioValue(msgTran);
                    break;
                case 0x81:
                    ProcessReadTag(msgTran);                  
                    break;                                      
                case 0x82:
                case 0x94:
                    ProcessWriteTag(msgTran); 
                    break;
                case 0x85:
                    ProcessSetAccessEpcMatch(msgTran);
                    break;
                case 0x86:
                    ProcessGetAccessEpcMatch(msgTran);
                    break;
                case 0x89:
                case 0x8B:
                    ProcessInventoryReal(msgTran);
                    break;
            }
        }

        const short writeMAXCnt = 2;
        public void WriteLEDGPIO(byte btReadId, byte btChooseGpio, byte btGpioValue)
        {
            short writeCnt = 0;
            int writeSuccessed = 0;
            int writeFailed = 0;
            int bWriteResult = -1;
            do
            {
                bWriteResult = reader.WriteGpioValue(btReadId, btChooseGpio, btGpioValue);
                Thread.Sleep(rfidSetting.LEDDelay);

                if (bWriteResult == 0)
                    writeSuccessed++;
                else
                {
                    writeFailed++;
#if DEBUG
                    Trace.Write("Retry write GPIO " + writeCnt + " rate " + writeFailed + " (" + writeSuccessed + ")");
#endif
                    Thread.Sleep(rfidSetting.LEDDelay);
                }
            } while ((bWriteResult != 0) && writeCnt < writeMAXCnt);           
        }

        enum LED
        {
            Green = 0x04,
            Red = 0x03
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
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Green, 0);                      
#if DEBUG
                        Trace.WriteLine("LED Green OFF ");
#endif
                    }
                    return;
                case LEDStatus.RedOff:
                    {
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Red, 0);                       
#if DEBUG
                        Trace.WriteLine("LED Red OFF ");
#endif
                    }
                    return;
                case LEDStatus.Off:
                    {
                        //if (resetGreenLED == LEDStatus.Green)
                        {
                            WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Green, 0);
                            WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Green, 0);
                        }
                        //if (resetRedLED == LEDStatus.Red)
                        {
                            WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Red, 0);
                            WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Red, 0);
#if DEBUG
                            Trace.WriteLine("LED Green OFF, LED Red OFF");
#endif
                        }
                    }
                    break;
                case LEDStatus.Red:
                    {
                       // if ((m_curSetting.btGpio4Value == 1 ) ||
                       //     (resetGreenLED == LEDStatus.Green))
                        {
                            WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Green, 0);                         
                        }
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Red, 1);                                         
                        Trace.WriteLine(checkTime() + "LED Red ON");                        
                    }
                    break;
                case LEDStatus.Green:
                    {
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Red, 0);                     
#if DEBUG
                        Trace.WriteLine("LED Red OFF, ");
#endif
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Green, 1);
                        Thread.Sleep(100);                 
                        Trace.WriteLine(checkTime() + "LED Green ON ");                       
                    }
                    break;
                case LEDStatus.BlinkingRed:
                    {                     
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Green, 0);                     
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Red, 1);
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Red, 0);
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Red, 0);
                        Trace.WriteLine(checkTime() + "Blinking LED Red ");
                    }
                    break;
                case LEDStatus.BlinkingGreen:
                    {                        
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Red, 0); 
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Green, 1);                
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Green, 0);
                        WriteLEDGPIO(m_curSetting.btReadId, (byte)LED.Green, 0);
                    }
                    break;
            }
        }

        void readRFIDTag(byte memoryBank, byte size, byte[] password)
        {
#if DEBUG
            Trace.WriteLine(checkTime() + "send read command " + memoryBank + " " + size);
#endif
            reader.ReadTag(m_curSetting.btReadId, memoryBank, 0, size, password);

            if(size > RFIDTagInfo.btErasecount)
                Thread.Sleep(rfidSetting.rwTagDelay * 3);
            else
                Thread.Sleep(rfidSetting.rwTagDelay);
        }

        private void verifyTag(string data, int index)
        {
            //Trace.WriteLine("Load Key " + symmetric.readKey());
            if (data.StartsWith(" "))
                data = data.Remove(0, 1) + " ";
            
            switch (tagState)
            {
                case readTagStatus.ReadReserve:
                    {
                        if ((data.Length / 3 == 4 || data.Length / 3 == 8) &&
                            (data.Substring(12).Trim().Contains(symmetric.readAccessCode().Trim())))
                        {
                            tagLists[index].tagReserve = data.Substring(0, 12);
                            tagState = readTagStatus.ReadReserveOK;
                            tagLists[index].verifiedFailCount = 0;
#if DEBUG
                            Trace.WriteLine(checkTime() + "read data ");
#endif
                            readRFIDTag(3, 30, RFIDTagInfo.accessCode);                         
                        }
                        else
                        {
                            tagState = readTagStatus.ReadReserveFailed;
                            tagLists[index].verifiedFailCount++;
                        }
                        return;
                    }
                case readTagStatus.Erased:
                    {
                        if (data.Length != RFIDTagInfo.btErasecount*6)
                            return;
                    }break;
                case readTagStatus.EraseConfirmed:
                case readTagStatus.Erasing:
                    return;

                case readTagStatus.ReadReserveOK:
                default:
                    if (data.Length < 120)
                    {
                        readRFIDTag(3, 30, RFIDTagInfo.accessCode);                      
#if DEBUG
                        Trace.WriteLine(checkTime() + "return");
#endif
                        return;
                    }
                    break;       
            }

            tagLists[index].tagData = data;
            if (tagState == readTagStatus.Erased)
            {         
                if(RFIDTagInfo.verifyData(data, false, true))
                {
                    tagState = readTagStatus.EraseConfirmed;
                    tagLists[index].tag_Status = RFIDTagData.TagStatus.VerifiedConfirm;
#if DEBUG
                    Trace.WriteLine(checkTime() + "Verified Erase data successful, " + data);                    
#endif
                }
                else
                {//re-erase tag                                          
                    Trace.WriteLine(checkTime() + "Verified Erase data failed"); //, id " + tagInfo.label);
                    readRFIDTag(3, RFIDTagInfo.btErasecount, RFIDTagInfo.accessCode);                   
                }
                return;
            }

            string decryptMsg = symmetric.DecryptFromHEX(tagLists[index].tagReserve, tagLists[index].tagData);
            if (decryptMsg != "")
            {
                //Trace.WriteLine("Decrypt data " + decryptMsg + ", Size " + decryptMsg.Length);               
                if (RFIDTagInfo.verifyData(decryptMsg, true, false))
                {
                    tagLists[index].verifiedFailCount = 0;
                    tagLists[index].tagDecryptedInfo = decryptMsg;
                    tagState = readTagStatus.VerifiedSuccessful;
#if DEBUG
                    Trace.WriteLine(checkTime() + "Verified data successful");
#endif
                    return;
                }
                else
                {
                    tagState = readTagStatus.VerifiedFailed;
                    tagLists[index].verifiedFailCount++;
                    Trace.WriteLine(checkTime() + "Verified data failed, " + decryptMsg +
                                        ", state " + tagState.ToString());                   
                }
            }
            else if (tagState != readTagStatus.ZeroData)
            {
                tagLists[index].verifiedFailCount++;
                Trace.WriteLine(checkTime() + "Verified data empty failed, state " + tagState.ToString());
                tagState = readTagStatus.VerifiedFailed;
            }

            byte[] zeroTmp = Enumerable.Repeat((byte)0x00, RFIDTagInfo.btErasecount * 2).ToArray();
            string input = CCommondMethod.ByteArrayToString(zeroTmp, 0, zeroTmp.Length);
            if (tagLists[index].tagData.Trim().StartsWith(input.Trim()))
            {
                Trace.WriteLine(checkTime() + " Data already been erased");
                tagState = readTagStatus.VerifiedFailed;
                tagLists[index].verifiedFailCount++;
                return;
            }
#if DEBUG
            Trace.WriteLine(checkTime() + " send read data ");
#endif
            readRFIDTag(3, 30, RFIDTagInfo.accessCode);           
        }
        /*
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
        }*/

        enum readTag
        {
            None,
            Error,
            Failed,
            Exception
        }

        private int findTagIndex(string RFIDTagID)
        {
            int index = -1;
            for (int i = 0; i < tagLists.Count; i++)
            {
                if (tagLists[i].EPC_ID.Trim().Contains(RFIDTagID))
                {
                    index = i;
                    break;
                }
            }
            return index;
        }

        void ProcessReadTag(Reader.MessageTran msgTran)
        {
            string strCmd = "Read Tag";
            string strErrorCode = string.Empty;
            try
            {
                //Trace.WriteLine("Read Tag, AryData.Length " + msgTran.AryData.Length, 0);
                if (msgTran.AryData.Length == 1)
                {
                    strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[0]);
                    if (readTagRetry++ < rwTagRetryMAX)
                    {
                        byte btWordCnt = 0;
                        RFIDTagInfo.accessCode = CCommondMethod.String2ByteArray(symmetric.readAccessCode(), 2, out btWordCnt);
                        Trace.WriteLine(checkTime() + "Retry read tag status: " + tagState.ToString()
                                        + " Read Tag retry: " + readTagRetry);
                        switch (tagState)
                        {
                            case readTagStatus.ReadReserve:
                            case readTagStatus.ReadReserveFailed:
                                readRFIDTag(0, 4, RFIDTagInfo.accessCode);
                                break;
                            case readTagStatus.Erased:
                                {
#if WRITE_RESERVE
                                    byte [] btAryPwd = new byte[4] { 0, 0, 0, 0 };
                                    reader.ReadTag(m_curSetting.btReadId, 0, 0, RFIDTagInfo.btErasecount, btAryPwd);
#else
                                    readRFIDTag(3, RFIDTagInfo.btErasecount, RFIDTagInfo.accessCode);
#endif
                                }
                                break;
                            case readTagStatus.EraseConfirmed:
                                {
                                    int index = 0;
                                    for (int i = 0; i < tagLists.Count; i++)
                                    {
                                        if (tagLists[i].tagDecryptedInfo != "")
                                        {
                                            index = i;
                                            break;
                                        }
                                    }
#if LOG_ENCRYPTED
                                    if (RFIDTagInfo.addLog(symmetric.Encrypt(RFIDTagInfo.getLogData(true, "", tagInfo))))
#else
                                    if (RFIDTagInfo.addLog(RFIDTagInfo.getLogData(true, "", tagLists[index])))
#endif
                                    {
                                        tagLists[index].tag_Status = RFIDTagData.TagStatus.GreenOn;
                                        setLEDstaus(LEDStatus.Green); //green on
                                        setLEDstaus(LEDStatus.Green); //green on                                       
                                        setLEDstaus(LEDStatus.Green); //green on  
                                        LEDTimerStart(true);
                                        tagState = readTagStatus.ZeroData;
                                    }
                                    break;
                                }
                            default:
                                readRFIDTag(3, 30, RFIDTagInfo.accessCode);                                
                                break;
                        }
                        Thread.Sleep(rfidSetting.rwTagDelay);
                        //setVerifiedLEDStatus(false, true); //red on
                    }
                    else
                    {
                        tagState = readTagStatus.Erasing;
                        readTagRetry = 0;
                        Trace.WriteLine(checkTime() + "retry read reach max " + tagState.ToString());
                        setLEDstaus(LEDStatus.BlinkingRed);
                    }
                }
                else
                {
                    readTagRetry = 0;
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

                    int index = findTagIndex(strEPC.Trim());
                    if (index == -1)
                    {
                        return;
                    }
                    
                    byte btWordCnt = 0;
                    tagLists[index].label = RFIDTagInfo.readEPCLabel(strEPC, out tagLists[index].EPC_PS_Num);
                    RFIDTagInfo.accessCode = CCommondMethod.String2ByteArray(symmetric.readAccessCode(), 2, out btWordCnt);                   
                                 
                    //read data section
                    try
                    {//find error in encrypted function, got 1 more bytes
                        //strData = lookupTable(strEPC, strData);
                        verifyTag(strData, index);
#if DEBUG
                        Trace.WriteLine(checkTime() + "read tag, State: " + tagState.ToString());
#endif
                        switch (tagState)
                        {
                            case readTagStatus.EraseConfirmed:
                                {
                                    //1. Erase verified success tag
                                    //2. add ink volume
#if LOG_ENCRYPTED
                                    if (RFIDTagInfo.addLog(symmetric.Encrypt(RFIDTagInfo.getLogData(true, "", tagInfo))))
#else
                                    if (RFIDTagInfo.addLog(RFIDTagInfo.getLogData(true, "", tagLists[index])))
#endif
                                    {
                                        tagLists[index].tag_Status = RFIDTagData.TagStatus.GreenOn;
                                        setLEDstaus(LEDStatus.Green); //green on
                                        setLEDstaus(LEDStatus.Green); //green on                                       
                                        setLEDstaus(LEDStatus.Green); //green on  
                                        LEDTimerStart(true);
                                        tagState = readTagStatus.ZeroData;                                        
                                    }
                                    break; 
                                }

                            case readTagStatus.VerifiedSuccessful:
                                {
#if READ2ERASE
                                    string[] tempList = tagLists[index].tagDecryptedInfo.Split(RFIDTagInfo.serialSep);
                                    UInt32 intToneVolume = Convert.ToUInt32(tempList[1].Substring(4, 4));
                                    if(RFIDTagInfo.addVolumeToFile(intToneVolume, 0, false))
                                    {
                                        tagState = readTagStatus.Erasing;
                                        byte[] btAryEpc = RFIDTagInfo.GetData(strEPC.ToUpper(), 2);
                                        reader.SetAccessEpcMatch(m_curSetting.btReadId, 0x00, Convert.ToByte(btAryEpc.Length), btAryEpc);
                                        Thread.Sleep(rfidSetting.rwTagDelay);
#if WRITE_RESERVE
                                        writeZeroTag(0, 0, RFIDTagInfo.btErasecount);
#else
                                        writeZeroTag(3, 0, RFIDTagInfo.btErasecount);
#endif
#if DEBUG
                                        Trace.WriteLine(checkTime() + "Send Erasing data ");
#endif
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
                                    break;
                                }
                                
                            case readTagStatus.ZeroData:
                                {
                                    messageCount = 0;
                                    setLEDstaus(LEDStatus.BlinkingGreen);
                                }
                                break;

                            case readTagStatus.VerifiedFailed:
                                {
                                    if (tagLists[index].verifiedFailCount > rwTagRetryMAX)
                                    {
                                        setLEDstaus(LEDStatus.Red);
                                        LEDTimerStart(true);
                                        tagLists[index].verifiedFailCount = 0;
                                    }
                                }
                                break;
                            default:
#if DEBUG
                                Trace.WriteLine(" read tag Skip!");
#endif
                                break;
                        }                      
                            
                    }
                    catch (Exception exp)
                    {
                        Trace.WriteLine(strCmd + " tag " + strEPC + " got Exception " + exp.Message +
                                            ", state " + tagState.ToString());
                        setLEDstaus(LEDStatus.BlinkingRed);                        
                    }                    
                }                        
            }
            catch (Exception exp)
            {
                Trace.WriteLine(strCmd + " got Exception " + exp.Message +
                                  ", state " + tagState.ToString());
                setLEDstaus(LEDStatus.BlinkingRed);
            }
        }
     
        private void writeZeroTag(byte btMemBank, byte btWordAddr, byte btWordCnt)
        {//user section to zero   //3,0,22/30 => 3, 0, 5 is enough
            byte btCmd = 0x94;
            byte[] zeroTmp = Enumerable.Repeat((byte)0x00, btWordCnt*2).ToArray();
#if DEBUG
            Trace.WriteLine(checkTime() + "send write zero command " + btWordCnt);
#endif
            reader.WriteTag(m_curSetting.btReadId, RFIDTagInfo.accessCode, btMemBank, 
                                btWordAddr, btWordCnt, zeroTmp, btCmd);
            Thread.Sleep(rfidSetting.rwTagDelay); //); //300ms delay          
        }
                
        void ProcessWriteTag(Reader.MessageTran msgTran)
        {
            string strCmd = "Write Tag";
            string strErrorCode = string.Empty;

            if (msgTran.AryData.Length == 1)
            {
                if (tagState == readTagStatus.EraseConfirmed)
                    return;

                strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[0]);
                Trace.WriteLine(checkTime() + strCmd + " Failure, Len==1: " + strErrorCode + " count " + writeTagRetry);

                if (writeTagRetry++ < rwTagRetryMAX)
                {
#if WRITE_RESERVE
			        writeZeroTag(0, 0, RFIDTagInfo.btErasecount);				       
#else
                    writeZeroTag(3, 0, RFIDTagInfo.btErasecount);                  
#endif
//#if DEBUG
                    Trace.WriteLine(checkTime() + "Write failed retry ");
//#endif                  
                }
                else
                {
                    writeTagRetry = 0;
//#if DEBUG
                    Trace.WriteLine(checkTime() + "Write tag failed (" + 
                                    rwTagRetryMAX.ToString() + "), state " + tagState.ToString());
//#endif
                    setLEDstaus(LEDStatus.BlinkingRed);
                }
            }
            else
            {
                int nLen = msgTran.AryData.Length;
                int nEpcLen = Convert.ToInt32(msgTran.AryData[2]) - 4;

                if (msgTran.AryData[nLen - 3] != 0x10)
                {
                    strErrorCode = CCommondMethod.FormatErrorCode(msgTran.AryData[nLen - 3]);                 
                    Trace.WriteLine(checkTime() + strCmd + " Failure, len-3!=0x10: " + strErrorCode);

                    writeZeroTag(3, 0, RFIDTagInfo.btErasecount);
                    Trace.WriteLine(checkTime() + "Write failed retry ");                   
                    writeTagRetry++;
                    return;
                }

                string strPC = CCommondMethod.ByteArrayToString(msgTran.AryData, 3, 2);
                string strEPC = CCommondMethod.ByteArrayToString(msgTran.AryData, 5, nEpcLen);
                string strCRC = CCommondMethod.ByteArrayToString(msgTran.AryData, 5 + nEpcLen, 2);
                string strData = string.Empty;

                byte byTemp = msgTran.AryData[nLen - 2];
                byte byAntId = (byte)((byTemp & 0x03) + 1);
                string strAntId = byAntId.ToString();
                string strReadCount = msgTran.AryData[nLen - 1].ToString();

                //erase success, then read back tag
                if (tagState == readTagStatus.Erasing)
                {
                    tagState = readTagStatus.Erased;
#if WRITE_RESERVE
                    byte[] btAryPwd = new byte[4] { 0, 0, 0, 0 };
                    reader.ReadTag(m_curSetting.btReadId, 0, 0, RFIDTagInfo.btErasecount, btAryPwd);
#else
                    readRFIDTag(3, RFIDTagInfo.btErasecount, RFIDTagInfo.accessCode);
#endif
                    Thread.Sleep(rfidSetting.rwTagDelay * 2);
                    Trace.WriteLine(checkTime() + "Write Successful -> verify erase data");
                    readTagRetry = 0;
                    writeTagRetry = 0;
                }
                return;
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
                Trace.WriteLine(checkTime() + strLog);

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
                Thread.Sleep(50);
            }
        }

        string checkTime()
        {
            DateTime aTime = DateTime.Now;
            return "Time spend: " + ((aTime.Ticks - startReading.Ticks)/10000000.0).ToString() + "s ";
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
                        //int nTotalRead = m_nTotal;// m_curInventoryBuffer.dtTagDetailTable.Rows.Count;
                        //TimeSpan ts = m_curInventoryBuffer.dtEndInventory - m_curInventoryBuffer.dtStartInventory;
                        //int nTotalTime = ts.Minutes * 60 * 1000 + ts.Seconds * 1000 + ts.Milliseconds;

                        if (m_nTotal % rfidSetting.m_nRealRate == 1)
                        {
                            foreach (DataRow row in m_curInventoryBuffer.dtTagTable.Rows)
                            {
                                //row[0] PC
                                //row[2] EPC serial number
                                //row[4] RSSI (value - 129)dBm
                                //row[6] frequency
                                //row[7] identification count
                                int index = -1;
                                string tagLabel = row[2].ToString();
                                int readCount = Convert.ToInt32(row[7]);
                                int rssi = Convert.ToInt32(row[4]);
                                if (tagLists.Count > 0)
                                {//update tag info
                                    index = findTagIndex(tagLabel.Trim());
                                }
                                if (index != -1)
                                {
                                    if (tagLists[index].readCount == readCount)
                                        tagLists[index].notUpdateCount++;
                                    else
                                        tagLists[index].notUpdateCount = 0;

                                    tagLists[index].readCount = readCount;
                                    tagLists[index].rssi = rssi;
                                }
                                else
                                {
                                    List<string> tagInfo = new List<string>();
                                  
                                    if (tagLabel.StartsWith(" "))
                                        tagLabel = tagLabel.Remove(0, 1) + " ";

                                    if (tagLabel.StartsWith("50 53"))
                                    {
                                        RFIDTagData tagData = new RFIDTagData();
                                        tagData.EPC_ID = tagLabel;//EPC serial number
                                        RFIDTagInfo.readEPCLabel(tagData.EPC_ID, out tagData.EPC_PS_Num);

                                        if (tagData.EPC_ID.StartsWith(" "))
                                            tagData.EPC_ID = tagData.EPC_ID.Remove(0, 1) + " ";

                                        tagData.readCount = readCount;
                                        tagData.rssi = rssi;
                                        tagLists.Add(tagData);
                                    }
                                }
                            }

                            //check all tag is update or not, if not, count not update time
                            for (int i = 0; i < tagLists.Count; i++)
                            {
#if DEBUG
                                if (tagLists[i].notUpdateCount++ > 50)
#else
                                if (tagLists[i].notUpdateCount++ > 5)
#endif
                                {//remove from database, listview, and taglist
                                    Trace.WriteLine(checkTime() + "Reset not update tag counter: " + tagLists[i].EPC_ID);
                                    tagLists[i].readCount = 0;
                                }
                                else if (tagLists[i].notUpdateCount++ > 20)

                                {//remove from database, listview, and taglist
                                    Trace.WriteLine(checkTime() + "Remove not update tag: " + tagLists[i].EPC_ID);
                                    m_curInventoryBuffer.removeInventoryItem(2, tagLists[i].EPC_ID);
                                    tagLists.RemoveAt(i);
                                }
                            }
#if SCAN2READ
                            try
                            {
                                if (tagLists.Count == 0)
                                {
                                    tagState = readTagStatus.Reading;
#if DEBUG
                                    Trace.WriteLine("Can't find tag, idle");
#endif
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
                                        string tagLabel = RFIDTagInfo.readEPCLabel(tagLists[i].EPC_ID, 
                                                                out tagLists[i].EPC_PS_Num);

                                        if ((readCount < tagLists[i].readCount) &&                                         
                                            (readRSSI < tagLists[i].rssi) && (tagLists[i].rssi > 60) &&
                                             (tagLists[i].tag_Status != RFIDTagData.TagStatus.GreenOn))
                                        {
                                            switch (tagState)
                                            {
                                                case readTagStatus.ZeroData:
                                                    if (tagLists[i].label != tagLabel)
                                                    {
                                                        tagState = readTagStatus.Reading;
                                                        readCount = tagLists[i].readCount;
                                                        readRSSI = tagLists[i].rssi;
                                                        maxCountIndex = i;
                                                    }
                                                    break;
                                                default:
                                                    {
                                                        readCount = tagLists[i].readCount;
                                                        readRSSI = tagLists[i].rssi;
                                                        maxCountIndex = i;
                                                    }
                                                    break;
                                            }
                                        }
                                    }

                                    //can't find good reading tag
                                    if (maxCountIndex < 0)
                                    {
                                        tagState = readTagStatus.Reading;
                                        goto Idle;
                                    }

                                    string strEPC = tagLists[maxCountIndex].EPC_ID;
                                    byte[] btAryEpc = RFIDTagInfo.GetData(strEPC.ToUpper(), 2);
                                    string convertLabel = 
                                        RFIDTagInfo.readEPCLabel(strEPC, out tagLists[maxCountIndex].EPC_PS_Num);
                                    if (convertLabel == "" || tagLists[maxCountIndex].EPC_PS_Num == 0)
                                    {//label format not correct
                                        tagState = readTagStatus.Reading;
                                        goto Idle;
                                    }
                                    else if (tagLists[maxCountIndex].tag_Status == RFIDTagData.TagStatus.GreenOn)
                                    {//verified
                                        Trace.Write("Fount verified tag, Current state: " + tagState + " go idle ");
                                        goto Idle;
                                    }
                                    else if ((tagLists[maxCountIndex].label != convertLabel) &&
                                             (tagLists[maxCountIndex].tag_Status != RFIDTagData.TagStatus.GreenOn))
                                    {
                                        tagState = readTagStatus.Reading;
                                        startReading = DateTime.Now;
                                        Trace.Write(startReading.ToString("MM/dd/yyyy HH:mm:ss: "));
                                        Trace.WriteLine("Found new tag " + strEPC);

                                        tagLists[maxCountIndex].label = convertLabel;
                                        m_curOperateTagBuffer.dtTagTable.Clear();
                                        readTagRetry = 0;
                                        writeTagRetry = 0;
                                    }

                                    //byte btWordAddr = Convert.ToByte(txtWordAddr.Text);                                         
                                    RFIDTagInfo.accessCode = RFIDTagInfo.GetPassword(symmetric.readAccessCode(), 2);                              
#if DEBUG
                                    Trace.WriteLine(checkTime() + "Refresh tag: " + tagState);
#endif
                                    iIdleCount= 0;
                                    switch (tagState)
                                    {
                                        case readTagStatus.ReadReserveOK:
                                            {//read full data
                                                byte[] btAryEpc1 = RFIDTagInfo.GetData(strEPC.ToUpper(), 2);
                                                reader.SetAccessEpcMatch(m_curSetting.btReadId, 0x00, Convert.ToByte(btAryEpc1.Length), btAryEpc1);
                                                Thread.Sleep(rfidSetting.rwTagDelay);

                                                readRFIDTag(3, 30, RFIDTagInfo.accessCode);
                                                break;
                                            }
                                        case readTagStatus.Erased:
                                            {//read erase only data
#if WRITE_RESERVE
                                                byte [] btAryPwd = new byte[4] { 0, 0, 0, 0 };
                                                reader.ReadTag(m_curSetting.btReadId, 0, 0, RFIDTagInfo.btErasecount, btAryPwd);
#else
                                                readRFIDTag(3, RFIDTagInfo.btErasecount, RFIDTagInfo.accessCode);
#endif
#if DEBUG
                                                Trace.WriteLine(checkTime() + "Send Erasing read data");
#endif
                                                //Thread.Sleep(rwTagDelay*2);
                                                break;
                                            }

                                        case readTagStatus.VerifiedFailed:
                                            if (tagLists[maxCountIndex].verifiedFailCount < rwTagRetryMAX)
                                            {
#if DEBUG
                                                Trace.WriteLine(checkTime() + "VerifiedFailed -> retry: " + strEPC);
#endif
                                                reader.SetAccessEpcMatch(m_curSetting.btReadId, 0x00, Convert.ToByte(btAryEpc.Length), btAryEpc);
                                                Thread.Sleep(rfidSetting.rwTagDelay);

                                                readRFIDTag(3, 30, RFIDTagInfo.accessCode);                                                
                                            }
                                            else
                                            {
                                                tagLists[maxCountIndex].verifiedFailCount = 0;
                                                setLEDstaus(LEDStatus.Red);
                                                LEDTimerStart(true);
                                            }
                                            break;
                                        case readTagStatus.ReadReserveFailed:                                            
                                        case readTagStatus.Reading:
                                            {                                              
                                                if (tagLists[maxCountIndex].verifiedFailCount < rwTagRetryMAX)
                                                {
#if DEBUG
                                                    Trace.WriteLine(checkTime() + "Reading or fail count: " + 
                                                                            tagLists[maxCountIndex].verifiedFailCount);
#endif
                                                    reader.SetAccessEpcMatch(m_curSetting.btReadId, 0x00, Convert.ToByte(btAryEpc.Length), btAryEpc);
                                                    Thread.Sleep(rfidSetting.rwTagDelay);

                                                    tagState = readTagStatus.ReadReserve;
                                                    readRFIDTag(0, 4, RFIDTagInfo.accessCode);
                                                }
                                                break;
                                            }
                                        case readTagStatus.Erasing:
                                            {
                                                reader.SetAccessEpcMatch(m_curSetting.btReadId, 0x00, Convert.ToByte(btAryEpc.Length), btAryEpc);
                                                Thread.Sleep(rfidSetting.rwTagDelay);

                                                Trace.WriteLine(checkTime() + "Erasing -> Write zero tag: " + strEPC);
                                                writeZeroTag(3, 0, RFIDTagInfo.btErasecount);
                                            }
                                            break;
                                        case readTagStatus.ReadReserve:
                                            {
                                                reader.SetAccessEpcMatch(m_curSetting.btReadId, 0x00, Convert.ToByte(btAryEpc.Length), btAryEpc);
                                                Thread.Sleep(rfidSetting.rwTagDelay);

                                                readRFIDTag(0, 4, RFIDTagInfo.accessCode);       
                                            }
                                            break;
                                        default:
                                            {
                                                readRFIDTag(0, 4, RFIDTagInfo.accessCode);
                                            }break;
                                    }
                                    return;
                                }
                            }
                            catch (System.Exception ex)
                            {
                                //MessageBox.Show(ex.Message);
                                Trace.WriteLine("Reading tag format not support Exception " + ex.Message);
                                setLEDstaus(LEDStatus.BlinkingRed); //red on
                            }
#endif
                        }                        
                        else
                        {                            
                            if(iIdleCount++ > 10)
                            {
                                LEDTimerStart(false);
                                iIdleCount = 0;
#if DEBUG
                                Trace.WriteLine(checkTime() + "Idle reset");                              
#endif
                            }
                            goto Idle;
                        }
                        Idle:
                        if (tagState == readTagStatus.Reading ||
                            tagState == readTagStatus.ZeroData)
                        {
#if DEBUG
                            Trace.WriteLine(checkTime() + "Idle blinking green");
#endif
                            messageCount = 0;
                            m_nTotal = 0;
                            setLEDstaus(LEDStatus.BlinkingGreen);
                        }
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
