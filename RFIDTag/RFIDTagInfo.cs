﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RFIDTag
{
    public class RFIDTagData
    {
        public string label = "";
        public string tagDecryptedInfo = "";
        public string tagReserve = "";
        public string tagData = "";

        public string EPC_ID;
        public ulong EPC_PS_Num;
        //public string EPC_data;
        public int readCount;
        public int notUpdateCount;
        public int rssi;
        public int verifiedFailCount;
        public TagStatus tag_Status;

        public RFIDTagData()
        {
            EPC_ID = "";
            EPC_PS_Num = 0;
            //EPC_data = "";
            readCount = 0;
            rssi = 0;
            verifiedFailCount = 0;
            tag_Status = TagStatus.Reading;
            label = "";
            tagDecryptedInfo = "";
            tagReserve = "";
            tagData = "";
        }
        public enum TagStatus
        {
            Reading,
            VerifiedConfirm,
            GreenOn
        }
    }
    
    public class RFIDTagInfo
    {
        public const int monthAllowed = 1;
        private const ulong tonerMAXVolume = 2000000000000; //max is 2L * 1000000000
        public const char serialSep = '=';
        public static byte[] accessCode = null;
        public static string inkSupportType = "0203,0201,";

        private static ulong currentTonerVolume = 0; //1L = 1,000,000,000,000 pL
        private static string labelFormat = "";
        private static string tonerVolumeFile = "";
        private static string inkAuthFile = "";
        private static string inkLogFile = "";
        private static string inkTypeFile = "";
        private static string inkFolder = "";
        public static UInt32 dongleID = 0;
        public static List<string> labelList = new List<string>();
        
        public static void loadFilePath(string folder, string filePath, 
                                        string fileInkFilePath, 
                                        string fileAuthInk, string _inkTypeFile)
        {
            inkFolder = folder;
            inkLogFile = folder + filePath;
            tonerVolumeFile = folder + fileInkFilePath;
            inkAuthFile = folder + fileAuthInk;
            inkTypeFile = folder + _inkTypeFile;

            if (!File.Exists(inkTypeFile))
            {//create file first
                using (StreamWriter sw = File.CreateText(inkTypeFile))
                {
                    sw.WriteLine(inkSupportType);
                    sw.Close();
                }
            }
        }

        public static void loadLabelFormat(byte[] lableFormatBytes)
        {
            labelFormat = Encoding.ASCII.GetString(lableFormatBytes);
        }

        public static string readLogFilePath()
        {
            return inkLogFile;
        }

        public static string readinkAuthFilePath()
        {
            return inkAuthFile;
        }

        public static string readinkTypeFile()
        {
            return inkTypeFile;
        }

        public static string readLabelFormat()
        {
            return labelFormat;
        }
       
        static bool writeByteToFile(byte[] encodeData)
        {
            try
            {
                using (FileStream fileStream =
                    new FileStream(tonerVolumeFile, 
                        FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    // Write the data to the file, byte by byte.
                    for (int i = 0; i < encodeData.Length; i++)
                    {
                        fileStream.WriteByte(encodeData[i]);
                    }
                    fileStream.Close();
                }
            }
            catch(Exception exp)
            {
                Trace.WriteLine("Get Exception " + exp.Message);
                Thread.Sleep(200);
                return false;
            }
            return true;
        }            
        protected static bool IsFileLocked(FileInfo file)
        {
            try
            {
                using (FileStream stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    stream.Close();
                }
            }
            catch (IOException)
            {
                //the file is unavailable because it is:
                //still being written to
                //or being processed by another thread
                //or does not exist (has already been processed)
                return true;
            }

            //file is not locked
            return false;
        }

        public static ulong readVolumeFile(out UInt32 dongleID)
        {
            //read only
            UInt32 _dongleID = 0;
            var readData = default(byte[]);
            ulong tmpVolume = 0;
            try
            {
                FileStream logFileStream = new FileStream(tonerVolumeFile,
                                            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using (StreamReader sr = new StreamReader(logFileStream))
                {
                    using (var memstream = new MemoryStream())
                    {
                        sr.BaseStream.CopyTo(memstream);
                        readData = memstream.ToArray();
                    }
                    // Clean up
                    sr.Close();
                    logFileStream.Close();
                }

                if (readData.Length != 0)
                {
                    tmpVolume = BlowFishImpl.decodeVolumeData(readData, out _dongleID);
                }
            }
            catch (Exception exp) { Trace.WriteLine("Read ink file got exception " + exp.Message); }
            dongleID = _dongleID;
            return tmpVolume;
        }

        public static bool addVolumeToFile(UInt32 intToneVolume, UInt32 dongleIDFromFile, bool bReadFile)
        {
            bool bWritedFile = false;
            try
            {//1. check file existed
             //2. read data inside
             //3. if read data is empty, write volume read from tag
             //4. if read data not empty, add the volume read from tag, if over max, set to max
                ulong tmpVolume = readVolumeFile(out dongleID);
                if (bReadFile && (dongleIDFromFile != dongleID))
                {
                    Trace.WriteLine("*** Dongle ID did not matched");
                    return false;
                }

                Trace.Write("read Dongle ID " + dongleID);
                byte[] defaultData = BlowFishImpl.encodeVolumeData((ulong)intToneVolume * 1000000000, dongleID);

                if (!File.Exists(tonerVolumeFile))
                {//create file first
                    bWritedFile = false;
                    using (StreamWriter sw = File.CreateText(tonerVolumeFile))
                    {
                        sw.WriteLine("");
                        sw.Close();
                    }

                    for (int i=0; i < 5 && !bWritedFile; i++)
                    {
                        bWritedFile = writeByteToFile(defaultData);
                        
                        if (bWritedFile)
                        {
                            Trace.WriteLine("Ink file not found, write " +
                                Encoding.Default.GetString(defaultData));
                            return true;
                        }
                    }
                    return false;
                }

                if (tmpVolume == 0)
                {//empty file
                    bWritedFile = false;
                    for (int i = 0; i < 5 && !bWritedFile; i++)
                    {
                        bWritedFile = writeByteToFile(defaultData);

                        if (bWritedFile)
                        {
                            Trace.WriteLine("Ink file empty, add " + intToneVolume.ToString() + "L");                           
                            return true;
                        }
                    }
                }
                else
                {
                    DateTime aTime = DateTime.Now;
                    Trace.Write(aTime.ToString("MM/dd/yyyy HH:mm:ss: "));

                    currentTonerVolume = tmpVolume + (ulong)intToneVolume * 1000000000;
                    Trace.Write("Ink file read " + currentTonerVolume.ToString() + ", ");

                    if (currentTonerVolume > tonerMAXVolume)
                        currentTonerVolume = tonerMAXVolume;

                    bWritedFile = false;
                    for (int i = 0; i < 5 && !bWritedFile; i++)
                    {
                        bWritedFile = writeByteToFile(BlowFishImpl.encodeVolumeData(currentTonerVolume, dongleID));

                        if (bWritedFile)
                        {
                            Trace.WriteLine("Ink file read, add " + currentTonerVolume.ToString() + "L");
                            return true;
                        }
                    }
                }
            }
            catch(Exception exp) { Trace.WriteLine("Get Exception " + exp.Message); }
            return false;
        }

        public static string getLogData(bool bUseTagInfo, string inputTagLabel, RFIDTagData tagInfo)
        {        
            string strHeadType = "02", strIntType = "03", strVolume = "1000",
                   strDate = "", strSupplier = "00", tagLabel = "";

            DateTime aTime = DateTime.Now;
            strDate = (Convert.ToInt32(aTime.ToString("MMyy")) + 1).ToString();             
            if (bUseTagInfo)
            {
                if (tagInfo.label == "" || tagInfo.tagDecryptedInfo == "")
                    return "";

                string[] tmpList = tagInfo.tagDecryptedInfo.Split(serialSep);
                for(int i=0; i< tmpList[1].Length; i+=2)
                {
                    switch (i)
                    {
                        case 0:
                            strHeadType = tmpList[1].Substring(0, 2);
                            break;
                        case 2:
                            strIntType = tmpList[1].Substring(2, 2);
                            break;
                        case 4:
                            strVolume = tmpList[1].Substring(4, 4);
                            break;
                        case 8:
                            strDate = tmpList[1].Substring(8, 4);
                            break;
                        case 12:
                            strSupplier = tmpList[1].Substring(12, 2);
                            break;
                    }
                }       
                tagLabel = tagInfo.label;
            }
            else
            {
                tagLabel = inputTagLabel;
            }

            string inputData = "";
            DateTime localDate = DateTime.Now;
            inputData = localDate.ToString("MM/dd/yy HH:mm:ss") + ",";
            inputData += tagLabel + "," + strHeadType + "," + strIntType + ",";
            inputData += strVolume + "," + strDate + "," + strSupplier + ",";
            inputData += readVolumeFile(out dongleID).ToString();
            return inputData;
        }

        public static bool addLog(string encryptedData)
        {
            if (encryptedData == "")
                return false;

            try
            {
                if (!File.Exists(inkLogFile))
                {
                    using (StreamWriter sw = File.CreateText(inkLogFile))
                    {
                        sw.WriteLine(encryptedData);
                        sw.Close();
                    }
                }
                else
                {
                    using (StreamWriter sw = File.AppendText(inkLogFile))
                    {
                        sw.WriteLine(encryptedData);
                        sw.Close();
                    }
                }
                return true;
            }
            catch (Exception exp) {
                Trace.WriteLine("Get Exception " + exp.Message);
                return false;
            }
        }

        //strAuthData
        //DateTime, Serial number, dongle ID
        public static int checkAuthDate(string strAuthDate, int monthAllowed)
        {//return 0, mean found the tag
         //return -1, mean verified failed
         //return 1, mean verified successful
            DateTime parsedDate = DateTime.Now;
            string strCompare = "MMyy";
            for (int i = 0; i < 2; i++)
            {
                switch(i)
                {
                    case 0://RFID tag
                        strCompare = "MMyy";
                        break;
                    case 1://RFID auth file
                        strCompare = "MMddyy";
                        break;
                }
                try
                {
                    parsedDate = DateTime.ParseExact(strAuthDate, strCompare, CultureInfo.InvariantCulture);
                    if (DateTime.Now > parsedDate.AddMonths(monthAllowed))
                        return -1;
                    else
                        return 1;
                }
                catch (FormatException exp)
                {
                    continue;
                }
            }
            return 0;         
        }

        public const byte btErasecount = 2;
        public static bool verifyData(string inputData, bool bVerifyData, bool bRead2Erase)
        {//1. input label = correct, bRead2Erase = false,
         //2. input label = 0, bRead2Erase = true;
            if (inputData == "")
                return false;
            try
            {
                if (bRead2Erase)
                {
                    ulong uData = 0;
                    if (ulong.TryParse(inputData.Substring(0, btErasecount).Replace(" ", ""), out uData))
                    {
                        if (uData == 0)
                            return true;
                    }
                    return false;
                }
                if (bVerifyData)
                {
                    string[] checkData = inputData.Split(RFIDTagInfo.serialSep);
                    if (checkData[0].StartsWith(labelFormat.Replace('_',' ').Trim()) )
                    {//1. check label
                     //2. check expire date
                     //3. check ink type
                        string inkType = checkData[1].Substring(0, 4);
                        string expireDate = checkData[1].Substring(8, 4);
                       
                        if (checkAuthDate(expireDate, monthAllowed) == 1)
                        {
                            if (File.Exists(inkTypeFile))
                            {
                                using (StreamReader sr = File.OpenText(inkTypeFile))
                                {
                                    string[] lines = sr.ReadLine().Split(',');
                                    if (Array.IndexOf(lines, inkType) >= 0)
                                    {
                                        return true;
                                    }
                                }
                            }                            
                        }                        
                    }
                    return false;
                }
                else
                {
                    for (int i = 0; i < labelFormat.Length; i++)
                    {
                        if (inputData[i] != labelFormat[i])
                        {
                            if (labelFormat[i] == labelFormat[labelFormat.Length-1])
                            {
                                continue;
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }
                }
            }
            catch (Exception exp) { Trace.WriteLine("Get Exception " + exp.Message); }           
            return false;
        }

        public static string ASCIIToHex(string input)
        {
            //return String.Concat(input.Select(x => ((int)x).ToString("x")));
            StringBuilder sb = new StringBuilder();
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);

            foreach (byte b in inputBytes)
            {
                sb.Append(string.Format("{0:x2} ", b));
            }
            return sb.ToString();
        }

        public static string readEPCLabel(string strEPC, out ulong uEPCNumber)
        {
            try
            {
                string EPClabel = RFIDTagInfo.HEXToASCII(strEPC.Substring(0, 6).ToUpper());
                string serialNumber = strEPC.Substring(7).Replace(" ", "");
                uEPCNumber = ulong.Parse(serialNumber);
                string EPSnumber = uEPCNumber.ToString("D20");
                return EPClabel + EPSnumber; ;
            }
            catch(Exception exp)
            {
                Trace.WriteLine("Get Exception " + exp.Message);
                uEPCNumber = 0;
                return "";
            }            
        }

        public static string HEXToASCII(string input)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < input.Length; i += 3)
            {
                var hexChar = input.Substring(i, 3).Trim();
                sb.Append((char)Convert.ToByte(hexChar, 16));
            }
            return sb.ToString();
        }

        public static string AddHexSpace(string input)
        {
            return String.Join(" ", Regex.Matches(input, @"\d{2}")
                        .OfType<Match>()
                        .Select(m => m.Value).ToArray());
        }

        static string BytesToString(byte[] bytes)
        {
            using (MemoryStream st = new MemoryStream(bytes))
            {
                using (StreamReader sr = new StreamReader(st))
                {
                    return sr.ReadToEnd();
                }
            }
        }

        public static Byte[] GetBytesFromBinaryString(String input)
        {
            var list = new List<Byte>();
            var binaryList = input.Split(' ');

            for (int i = 0; i < binaryList.Length; i++)
            {
                list.Add(Convert.ToByte(binaryList[i], 2));
            }
            return list.ToArray();
        }

        public static Byte[] GetPassword(string strValue, int nLength)
        {
            string[] reslut = CCommondMethod.StringToStringArray(strValue, nLength);
            return CCommondMethod.StringArrayToByteArray(reslut, reslut.Length);
        }

        public static Byte[] GetData(string strValue, int nLength)
        {
            string[] reslut = CCommondMethod.StringToStringArray(strValue.ToUpper(), nLength);
            return CCommondMethod.StringArrayToByteArray(reslut, reslut.Length);
        }
    }
}
