#define LOG_ENCRYPTED
using System;
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
    public class tagInfo
    {
        public static string label = "";
        public static string tagDecryptedInfo = "";
        public static string tagReserve = "";
        public static string tagData = "";
        public static bool bVerified = false;
    }
        
    public class RFIDTagInfo
    {
        private const ulong tonerMAXVolume = 2000000000000; //max is 2L * 1000000000
        private static ulong currentTonerVolume = 0; //1L = 1,000,000,000,000 pL
        private static string labelFormat = "";
        private static string tonerVolumeFile = "";
        private static string inkAuthFile = "";       
        public const char serialSep = '=';        
        public static List<string> labelList = new List<string>();
        
        public static void loadVolumeFilePath(string filePath)
        {
            tonerVolumeFile = filePath;
        }

        public static void loadinkAuthFilePath(string filePath)
        {
            inkAuthFile = filePath;
        }

        public static void loadLabelFormat(byte[] lableFormatBytes)
        {
            labelFormat = Encoding.ASCII.GetString(lableFormatBytes);
        }

        public static string readinkAuthFile()
        {
            return inkAuthFile;
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
            catch(Exception e)
            {
                Thread.Sleep(500);
                return false;
            }
            return true;
        }            

        public static void addTempInkAuth()
        {           
            try
            {
                File.Delete(inkAuthFile);
                addVolumeToFile(1000);
            }
            catch(Exception exp) { }
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

        public static ulong readVolumeFile()
        {
            //read only   
            var readData = default(byte[]);
            ulong tmpVolume = 0;
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
                tmpVolume = BlowFishImpl.decodeVolumeData(readData);
            }
            return tmpVolume;
        }

        public static bool addVolumeToFile(int intToneVolume)
        {
            bool bWritedFile = false;
            try
            {//1. check file existed
             //2. read data inside
             //3. if read data is empty, write volume read from tag
             //4. if read data not empty, add the volume read from tag, if over max, set to max

                byte[] defaultData = BlowFishImpl.encodeVolumeData((ulong)intToneVolume * 1000000000);
                if (!File.Exists(tonerVolumeFile))
                {
                    bWritedFile = false;
                    for(int i=0; i < 5 && !bWritedFile; i++)
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

                ulong tmpVolume = readVolumeFile();
                if (tmpVolume == 0)
                {//empty file
                    bWritedFile = false;
                    for (int i = 0; i < 5 && !bWritedFile; i++)
                    {
                        bWritedFile = writeByteToFile(defaultData);

                        if (bWritedFile)
                        {
                            Trace.WriteLine("Ink file empty, write " +
                                Encoding.Default.GetString(defaultData));
                            return true;
                        }
                    }
                }
                else
                {                    
                    currentTonerVolume = tmpVolume + (ulong)intToneVolume * 1000000000;
                    Trace.WriteLine("Ink file read " + tmpVolume.ToString());

                    if (currentTonerVolume > tonerMAXVolume)
                        currentTonerVolume = tonerMAXVolume;

                    bWritedFile = false;
                    for (int i = 0; i < 5 && !bWritedFile; i++)
                    {
                        bWritedFile = writeByteToFile(BlowFishImpl.encodeVolumeData(currentTonerVolume));

                        if (bWritedFile)
                        {
                            Trace.WriteLine(", Ink file is update, write " +
                                Encoding.Default.GetString(BlowFishImpl.encodeVolumeData(currentTonerVolume)));
                            return true;
                        }
                    }
                }
            }
            catch(Exception exp) { }
            return false;
        }

        public static string getLogData(bool bUseTagInfo, string inputTagLabel)
        {        
            string strHeadType = "", strIntType = "", strVolume = "",
                   strDate = "", strSupplier = "", tagLabel = "";

            if (bUseTagInfo)
            {
                if (tagInfo.label == "" || tagInfo.tagDecryptedInfo == "")
                    return "";

                string[] tmpList = tagInfo.tagDecryptedInfo.Split(serialSep);
                strHeadType = tmpList[1].Substring(0, 2);
                strIntType = tmpList[1].Substring(2, 2);
                strVolume = tmpList[1].Substring(4, 4);
                strDate = tmpList[1].Substring(8, 4);
                strSupplier = tmpList[1].Substring(12, 2);
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
            inputData += readVolumeFile().ToString();
            return inputData;
        }

        public static void addLog(string encryptedData)
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            path += "\\RFIDTag";

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            if (encryptedData == "")
                return;

            try
            {
#if LOG_ENCRYPTED
                using (StreamWriter sw = File.AppendText(path + "\\log.dat"))
                {
                    sw.WriteLine(encryptedData);
                    sw.Close();
                }
#else
                using (StreamWriter sw = File.AppendText(path + "\\log.dat"))
                {
                    sw.WriteLine(inputData);
                    sw.Close();
                }
#endif
                }
            catch (Exception exp) { }
        }

        //strAuthData
        //DateTime, Serial number, dongle ID
        public static int checkAuthDateAndLog(string strAuthData, string plaintext, int dayAllowed)
        {//return 0, mean found the tag
         //return -1, mean verified failed
         //return 1, mean verified successful

            DateTime parsedDate = DateTime.Now;
            try
            {
                string[] authDataArry = strAuthData.Split(',');
                string[] logArry = plaintext.Split(',');

                if (string.Compare(authDataArry[1], logArry[1]) == 0)
                    return 0;

                parsedDate = DateTime.ParseExact(authDataArry[0], "MMddyy", CultureInfo.InvariantCulture);                
                //DateTime.TryParse(authDataArry[0], out parsedDate);

                DateTime ExpireDay = DateTime.Now.AddDays(dayAllowed);
                string strExpireDate = ExpireDay.ToString("MMddyy");

                if (ExpireDay < parsedDate)
                    return -1;
                else
                    return 1;
            }
            catch (FormatException)
            {
                return 0;
            }           
        }

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
                    if (ulong.TryParse(inputData.Replace(" ", ""), out uData))
                    {
                        if (uData == 0)
                            return true;
                    }
                    return false;
                }
                if (bVerifyData)
                {
                    string[] checkData = inputData.Split(RFIDTagInfo.serialSep);
                    if (checkData[0].StartsWith("PS"))
                    {
                        return true;
                    }
                    return false;
                }
                else
                {
                    for (int i = 0; i < labelFormat.Length; i++)
                    {
                        if (inputData[i] != labelFormat[i])
                        {
                            if (labelFormat[i] == '_')
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
            catch (Exception exp) { }           
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

        public static string readEPCLabel(string strEPC)
        {
            try
            {
                string EPClabel = RFIDTagInfo.HEXToASCII(strEPC.Substring(0, 6).ToUpper());
                string serialNumber = strEPC.Substring(7).Replace(" ", "");
                ulong uEPCNumber = ulong.Parse(serialNumber);
                string EPSnumber = uEPCNumber.ToString("D20");
                return EPClabel + EPSnumber; ;
            }
            catch(Exception e)
            {
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
