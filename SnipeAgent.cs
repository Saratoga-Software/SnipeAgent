using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Configuration;
using System.Management;
using System.Diagnostics;
using System.DirectoryServices.AccountManagement;
using System.Collections.Specialized;
using SnipeSharp;
using SnipeSharp.Endpoints.Models;
using SnipeSharp.Endpoints.SearchFilters;
using Microsoft.Win32;
using SnipeSharp.Common;
using System.ComponentModel;
using License = SnipeSharp.Endpoints.Models.License;
using System.Runtime.InteropServices;
using Hardware.Info;


namespace SnipeAgent
{

    public class PCSystemTypes {
        public Dictionary<string, string> SystemTypes;
    }

    public class WindowsSystemTypes : PCSystemTypes
    {
        public WindowsSystemTypes()
        {
            this.SystemTypes = new Dictionary<string, string>();
            this.SystemTypes.Add("0", "Undefined");
            this.SystemTypes.Add("1", "Desktop");
            this.SystemTypes.Add("2", "Laptop");
            this.SystemTypes.Add("3", "Workstation");
            this.SystemTypes.Add("4", "Enterprise Server");
            this.SystemTypes.Add("5", "SOHO Server");
            this.SystemTypes.Add("6", "Appliance PC");
            this.SystemTypes.Add("7", "Performance Server");
            this.SystemTypes.Add("8", "Maximum");
        }
    }

    public class Sentry // Data acquissition
    {
        private Dictionary<string, List<string>> Queries; // Where key = query type, value = query itself
        private Dictionary<string, string> Values; // Internal representation of query results
        private System.Collections.Specialized.NameValueCollection Settings;
        public Dictionary<string, string> rawResults // Public representation of query results - raw values. Useful for debug
        {
            get { return this.Values; }
        }

        private readonly HardwareInfo hardware = new HardwareInfo();

        public Sentry(System.Collections.Specialized.NameValueCollection appSettings) // constructor 
        {
            Queries = new Dictionary<string, List<string>>();
            Settings = appSettings;
        }

        public Location GetLocation(NameValueCollection appSettings, SnipeItApi snipe)
        {
            string assetLocation = this.Values["Location"];
            Location currentLocation = new Location(assetLocation);
            return currentLocation;
        }

        public StatusLabel GetStatusLabel(NameValueCollection appSettings, SnipeItApi snipe)
        {
            string defaultLabel = appSettings["DefaultStatusLabel"];
            StatusLabel defaultStatusLabel = new StatusLabel(defaultLabel);
            return defaultStatusLabel;
        }

        public Company GetCompany(NameValueCollection appSettings, SnipeItApi snipe)
        {
            string companyName = appSettings["Company"];
            Company currentCompany = new Company(companyName);
            return currentCompany;
        }

        public Category GetCategory(NameValueCollection appSettings, SnipeItApi snipe)
        {
            hardware.RefreshBIOSList();
            hardware.RefreshCPUList();
            string systemType = GetOutputVariable("Win32_ComputerSystem.PCSystemType");
            // TODO: Place in a separate enum class:
            WindowsSystemTypes winTypes = new WindowsSystemTypes();
            string systemTypeFull = "Undefined";
            try
            {
                systemTypeFull = winTypes.SystemTypes[systemType];
            }
            catch (Exception e)
            {
                Trace.WriteLine("Exception encountered while processing WinSystemType: " + e.ToString());
            }
            Category currentCategory = new Category(systemTypeFull);
            return currentCategory;
        }

        public Manufacturer GetManufacturer(NameValueCollection appSettings, SnipeItApi snipe)
        {
            var manufacturer = hardware.ComputerSystemList.FirstOrDefault().Vendor;

            Manufacturer systemManufacturer = new Manufacturer(manufacturer);
            return systemManufacturer;
        }

        public Model GetModel(NameValueCollection appSettings, SnipeItApi snipe)
        {
            string modelTotal = hardware.ComputerSystemList.FirstOrDefault().Name;
            // TODO: This only works is in the exact format "ModelName ModelNumber"
            List<String> modelFragments = modelTotal.Split(' ').ToList();
            string modelNumber = modelFragments[modelFragments.Count() - 1];
            string modelMake = modelFragments[0];

            Model currentModel = new Model
            {
                Name = modelTotal,
                Manufacturer = null,
                Category = null,
                ModelNumber = modelNumber,
            };
            return currentModel;
        }

        public List<License> GetLicenses(NameValueCollection appSettings, SnipeItApi snipe)
        {
            var licenses = new List<License>();

            foreach (var value in this.Values.Where(k => k.Key.Contains("SoftwareLicensingProduct.Name.")))
            {
                var count = value.Key.Remove(0, value.Key.LastIndexOf('.')+1);
                if (!this.Values[$"SoftwareLicensingProduct.ProductKeyID.{count}"].Contains("undefined"))
                { 
                    licenses.Add(new License { 
                        Name = this.Values[$"SoftwareLicensingProduct.Name.{count}"],
                        ProductKey = this.Values[$"SoftwareLicensingProduct.ProductKeyID.{count}"],
                        Seats = 1,
                        FreeSeatsCount = 0
                    });
                }
            }
            
            return licenses;
        }

        public Asset GetAsset(NameValueCollection appSettings, SnipeItApi snipe)
        {
            hardware.RefreshComputerSystemList();
            hardware.RefreshNetworkAdapterList();
            string systemName = Environment.MachineName;
            string serialNumber = hardware.ComputerSystemList.FirstOrDefault().IdentifyingNumber;
            string macAddress = hardware.NetworkAdapterList.Where(na => na.AdapterType.Contains("Ethernet 802.3") && na.BytesReceivedPersec > 0 && na.BytesSentPersec > 0).FirstOrDefault().MACAddress;
            Dictionary<string, string> customFields = new Dictionary<string, string>();
            customFields.Add("_snipeit_mac_address_1", macAddress);
            string warrantyMonths = appSettings["WarrantyMonths"];

            bool isInteractive = false;
            bool interactiveParseSuccess = Boolean.TryParse(appSettings["Interactive"], out isInteractive);
            if (interactiveParseSuccess && isInteractive)
            {
                Console.WriteLine("Enter the computer name: ");
                systemName = Console.ReadLine();
            }
            
            Asset currentComputer = new SnipeSharp.Endpoints.Models.Asset
            {
                Company = null,
                AssetTag = appSettings["AssetTagPrefix"] + "-" + serialNumber, // <-- to be implemented.. somehow, somewhere
                Model = null,
                StatusLabel = null,
                RtdLocation = null,
                Name = systemName,
                Serial = serialNumber,
                WarrantyMonths = warrantyMonths,
                CustomFields = customFields,
            };

            return currentComputer;
        }

        public void AddQuery(string queryType, string queryString) { // safely addes queries to the queryList monstrocity, built for expandability (c) 
            List<string> queryList = new List<string>();


            if (this.Queries.ContainsKey(queryType))
            {
                queryList = Queries[queryType];
                queryList.Add(queryString);
                this.Queries[queryType] = queryList;
                return;
            } else
            {
                queryList.Add(queryString);
                this.Queries.Add(queryType, queryList);
                return;
            }
        }


        private void RunWMI() { // runs all WMI queries

            Dictionary<string, string> resultDictionary = new Dictionary<string, string>();
            ManagementObjectCollection queryCollection;

            //Query system for Operating System information
            foreach (string wmiQuery in this.Queries["WMI"])
            {
                int count = 0;

                SelectQuery selectQuery = new SelectQuery(wmiQuery);
                ManagementObjectSearcher searcher = new ManagementObjectSearcher(selectQuery);
            

                queryCollection = searcher.Get();

                foreach (ManagementObject m in queryCollection)
                {
                    // Display all properties.
                    foreach (PropertyData property in m.Properties)
                    {
                        string propertyValue = "<undefined>";
                        if  (property.Value != null && !String.IsNullOrWhiteSpace(property.Value.ToString()))
                        {
                            propertyValue = property.Value.ToString().Trim();
                        }
                        if (!resultDictionary.ContainsKey(selectQuery.ClassName + "." + property.Name))
                        {
                            resultDictionary.Add(selectQuery.ClassName + "." + property.Name, propertyValue);
                        }
                        else
                        {
                            resultDictionary.Add(selectQuery.ClassName + "." + property.Name + "." + count.ToString(), propertyValue);
                        }
                    }
                    count++;
                }
            }

            this.Values = resultDictionary;
        }

        //private void RunHardware()
        //{
        //    Dictionary<string, string> resultDictionary = new Dictionary<string, string>();

            

        //    foreach (var s in hardware.ComputerSystemList)
        //    {
        //        this.Values.Add("ComputerSystem.Name", s.Name);
        //        this.Values.Add("ComputerSystem.Manufacturer", s.Vendor);
        //        this.Values.Add("ComputerSystem.Model", s.SKUNumber);
        //        this.Values.Add("ComputerSystem.SerialNumber", s.IdentifyingNumber);
        //    }

            

        //    foreach (var b in hardware.BiosList)
        //    {
        //        this.Values.Add("Bios.Name", b.Name);
        //        this.Values.Add("Bios.Manufacturer", b.Manufacturer);
        //    }
        //}


        private void RunLocation() // Runs all code related to location & location sources
        {
            string location_string = "";
            foreach (string locationQuery in this.Queries["Location"])
            {
                if (locationQuery == "OU")
                {
                    try
                    {
                        int ouLevel;
                        bool ouLevelSuccess = int.TryParse(Settings["OULevel"], out ouLevel);
                        if (!ouLevelSuccess)
                        {
                            ouLevel = 1;
                        }
                        string[] machineOU;
                        using (var context = new PrincipalContext(ContextType.Domain))
                        using (var comp = ComputerPrincipal.FindByIdentity(context, Environment.MachineName))
                            machineOU = comp.DistinguishedName.Split(',').SkipWhile(s => !s.StartsWith("OU=")).ToArray();

                        location_string = machineOU[0].Split('=')[ouLevel];
                    }
                    catch (Exception e)
                    {
                        Trace.WriteLine("Could not get location from OU");
                        Trace.WriteLine(e.ToString());
                        Trace.WriteLine("Getting location from config file instead");
                        location_string = Settings["Location"];
                    }
                } else
                {
                    location_string = Settings["Location"];

                }
            }
            this.Values.Add("Location", location_string);

        }

        public string GetOutputVariable(string key)
        {
            if (this.Values.ContainsKey(key))
            {
                return this.Values[key];
            } else
            {
                return "";
            }
        }

        public string GetFormattedVariable(string key, string variable = "", string format="<name>=<var>") // produces formatted output, supposed to throw exception if no results in raw results
        {
            if (String.IsNullOrEmpty(variable))
            {
                format = "<var>";
            }
            if (this.Values.ContainsKey(key))
            {
                return format.Replace("<var>", this.Values[key]).Replace("<name>",variable);
            } else
            {
                return "ERROR: key \"" + key + "\" not found in the results of the query";
            }
        }



        public void Run() // supposed to run all queries of all types and handle per-type errors
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                this.RunWMI();
            }
            this.RunLocation();
        }

    }



    class SnipeAgent
    {
        static void Main(string[] args)
        {
            Trace.WriteLine(DateTime.Now.ToShortDateString() + " " + DateTime.Now.ToLongTimeString() + ": Started application.");

            var debugTimer = new Stopwatch();
            System.Collections.Specialized.NameValueCollection appSettings = System.Configuration.ConfigurationManager.AppSettings;
            debugTimer.Start(); 

            ApiSettings apiSettings = new ApiSettings();
            apiSettings.ApiToken = appSettings["API"];
            apiSettings.BaseUrl = new Uri(appSettings["BaseURI"]);
            SnipeItApi snipe = new SnipeItApi(apiSettings);

            Sentry mySentry = new Sentry(appSettings); // creating new Sentry (we can have multiple for parallel execution at a later point)

            // Adding what we want
            mySentry.AddQuery("WMI", "SELECT PCSystemType FROM Win32_ComputerSystem");
            //mySentry.AddQuery("WMI", "SELECT IdentifyingNumber FROM Win32_ComputerSystemProduct");
            //mySentry.AddQuery("WMI", "SELECT Name FROM Win32_BIOS");
            //mySentry.AddQuery("WMI", "SELECT Manufacturer,Name,MACAddress FROM Win32_NetworkAdapter WHERE NetEnabled=true AND AdapterTypeId=0 AND netConnectionStatus=2");
            //mySentry.AddQuery("WMI", "SELECT Manufacturer,Model,SerialNumber FROM Win32_DiskDrive WHERE Index=0");
            //mySentry.AddQuery("WMI", "SELECT EndingAddress FROM Win32_MemoryArray");
            //mySentry.AddQuery("WMI", "SELECT Name FROM Win32_DesktopMonitor");
            //mySentry.AddQuery("WMI", "SELECT Manufacturer,Product,SerialNumber FROM Win32_BaseBoard");
            //mySentry.AddQuery("WMI", "SELECT Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor");
            mySentry.AddQuery("WMI", "SELECT Name,ProductKeyID FROM SoftwareLicensingProduct");

            bool getOU = false;
            bool getOUSuccess = Boolean.TryParse(appSettings["OUEnabled"], out getOU);
            if (getOUSuccess && getOU)
            {
                mySentry.AddQuery("Location", "OU");
            } else
            {
                mySentry.AddQuery("Location", "Config");
            }

            mySentry.Run();

            Asset currentAsset = mySentry.GetAsset(appSettings, snipe);
            Model currentModel = mySentry.GetModel(appSettings, snipe);
            Manufacturer currentManufacturer = mySentry.GetManufacturer(appSettings, snipe);
            Category currentCategory = mySentry.GetCategory(appSettings, snipe);
            Company currentCompany = mySentry.GetCompany(appSettings, snipe);
            StatusLabel currentStatusLabel = mySentry.GetStatusLabel(appSettings, snipe);
            Location currentLocation = mySentry.GetLocation(appSettings, snipe);
            List<License> currentLicenses = mySentry.GetLicenses(appSettings, snipe);

            //Broker.syncAsset(snipe, currentComputer);
            Broker snipeBroker = new Broker();
            bool connectionStatus = snipeBroker.CheckConnection(appSettings);

            if (connectionStatus)
            {
                snipeBroker.SyncAll(snipe, currentAsset, currentModel, currentManufacturer, currentCategory,
                                    currentCompany, currentStatusLabel, currentLocation, currentLicenses);
            } else {
                Console.WriteLine("ERROR: Could not connect to SnipeIT database instance.");
                // Until a standardized logging framework is set up, quick way to make user see crash message.
                Console.ReadKey();
            }

            debugTimer.Stop();
            Trace.WriteLine("Total program execution time " + debugTimer.ElapsedMilliseconds + "ms.");
            Trace.WriteLine(DateTime.Now.ToShortDateString() + " " + DateTime.Now.ToLongTimeString() + ": Exiting application.");
            Trace.WriteLine(" ");
        }
    }
}
