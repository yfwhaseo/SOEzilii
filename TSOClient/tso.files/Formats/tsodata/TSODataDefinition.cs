﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace FSO.Files.Formats.tsodata
{
    public class TSODataDefinition
    {
        private List<List1Entry> List1;
        private List<List1Entry> List2;
        private List<List1Entry> List3;
        public List<StringTableEntry> Strings;

        public Struct[] Structs;
        public DerivedStruct[] DerivedStructs;

        private Dictionary<string, Struct> StructsByName = new Dictionary<string, Struct>();

        public void Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream))
            {
                var fileID = reader.ReadUInt32();
                this.List1 = ReadList(reader, false);
                this.List2 = ReadList(reader, false);
                this.List3 = ReadList(reader, true);

                var numStrings = reader.ReadUInt32();
                this.Strings = new List<StringTableEntry>();
                for (var i = 0; i < numStrings; i++)
                {
                    var stringEntry = new StringTableEntry();
                    stringEntry.ID = reader.ReadUInt32();
                    stringEntry.Value = ReadNullTerminatedString(reader);
                    stringEntry.Unknown = reader.ReadByte();
                    this.Strings.Add(stringEntry);
                }
            }

            var Structs = new List<Struct>();
            var DerivedStructs = new List<DerivedStruct>();


            foreach (var item in List1){
                var fields = new List<StructField>();

                foreach(var field in item.Entries){
                    if (field.TypeStringID == 0xA99AF3AC) Console.WriteLine("unknown value: " + GetString(item.NameStringID) + "::"+ GetString(field.NameStringID));
                    fields.Add(new StructField {
                        ID = field.NameStringID,
                        Name = GetString(field.NameStringID),
                        TypeID = field.TypeStringID,
                        Classification = (StructFieldClassification)field.Unknown,
                        ParentID = item.NameStringID
                    });
                }

                Structs.Add(new Struct {
                    ID = item.NameStringID,
                    Name = GetString(item.NameStringID),
                    Fields = fields.ToList()
                });
            }

            foreach (var item in List2)
            {
                var fields = new List<StructField>();

                foreach (var field in item.Entries)
                {
                    if (field.TypeStringID == 0xA99AF3AC) Console.WriteLine("unknown value: " + GetString(item.NameStringID) + "::" + GetString(field.NameStringID));
                    fields.Add(new StructField
                    {
                        ID = field.NameStringID,
                        Name = GetString(field.NameStringID),
                        TypeID = field.TypeStringID,
                        Classification = (StructFieldClassification)field.Unknown,
                        ParentID = item.NameStringID
                    });
                }

                Structs.Add(new Struct
                {
                    ID = item.NameStringID,
                    Name = GetString(item.NameStringID),
                    Fields = fields.ToList()
                });
            }

            foreach (var item in List3)
            {
                var fields = new List<DerivedStructFieldMask>();

                foreach (var field in item.Entries)
                {
                    if (field.TypeStringID == 0xA99AF3AC) Console.WriteLine("unknown value: " + GetString(item.NameStringID) + "::" + GetString(field.NameStringID));
                    fields.Add(new DerivedStructFieldMask
                    {
                        ID = field.NameStringID,
                        Name = GetString(field.NameStringID),
                        Type = (DerivedStructFieldMaskType)field.Unknown
                    });
                }

                DerivedStructs.Add(new DerivedStruct
                {
                    ID = item.NameStringID,
                    Parent = item.ParentStringID,
                    Name = GetString(item.NameStringID),
                    FieldMasks = fields.ToArray()
                });
            }

            this.Structs = Structs.ToArray();
            this.DerivedStructs = DerivedStructs.ToArray();

            foreach(var _struct in Structs)
            {
                StructsByName.Add(_struct.Name, _struct);
            }

            InjectStructs();
        }

        private void InjectStructs()
        {
            //this is just an example of how to do this.
            //todo: a format we can easily create and read from to provide these new fields

            StructsByName["Lot"].Fields.Add(new StructField()
            {
                Name = "Lot_SkillGamemode",
                ID = 0xaabbccdd,
                Classification = StructFieldClassification.SingleField,
                ParentID = StructsByName["Lot"].ID,
                TypeID = 1768755593 //uint32
            });

            var fields = DerivedStructs[17].FieldMasks.ToList();
            fields.Add(new DerivedStructFieldMask()
            {
                ID = 0xaabbccdd,
                Name = "Lot_SkillGamemode",
                Type = DerivedStructFieldMaskType.KEEP
            });
            DerivedStructs[17].FieldMasks = fields.ToArray();
        }

        public Struct GetStructFromValue(object value)
        {
            if (value == null) { return null; }
            return GetStruct(value.GetType());
        }

        public Struct GetStruct(Type type)
        {
            return GetStruct(type.Name);
        }

        public Struct GetStruct(string name)
        {
            if (StructsByName.ContainsKey(name))
            {
                return StructsByName[name];
            }
            return null;
        }

        public Struct GetStruct(uint id)
        {
            return Structs.FirstOrDefault(x => x.ID == id);
        }


        private string GetString(uint id)
        {
            var item = Strings.FirstOrDefault(x => x.ID == id);
            if (item == null)
            {
                return null;
            }
            return item.Value;
        }

        private string GetString(List<StringTableEntry> strings, uint id)
        {
            var item = strings.FirstOrDefault(x => x.ID == id);
            if (item == null)
            {
                return "";
            }
            return item.Value;
        }

        private string ReadNullTerminatedString(BinaryReader reader)
        {
            var result = "";
            while (true)
            {
                var ch = (char)reader.ReadByte();
                if (ch == '\0')
                {
                    break;
                }
                else
                {
                    result += ch;
                }
            }
            return result;
        }

        private List<List1Entry> ReadList(BinaryReader reader, bool parentID)
        {
            var list1Count = reader.ReadUInt32();

            var list1 = new List<List1Entry>();
            for (int i = 0; i < list1Count; i++)
            {
                var entry = new List1Entry();
                entry.NameStringID = reader.ReadUInt32();
                if (parentID == true)
                {
                    entry.ParentStringID = reader.ReadUInt32();
                }
                entry.Entries = new List<List1EntryEntry>();

                var subEntryCount = reader.ReadUInt32();
                for (int y = 0; y < subEntryCount; y++)
                {
                    var subEntry = new List1EntryEntry();
                    subEntry.NameStringID = reader.ReadUInt32();
                    subEntry.Unknown = reader.ReadByte();
                    if (parentID == false)
                    {
                        subEntry.TypeStringID = reader.ReadUInt32();
                    }
                    entry.Entries.Add(subEntry);
                }

                list1.Add(entry);
            }
            return list1;
        }
    }

    public class StringTableEntry
    {
        public uint ID;
        public string Value;
        public byte Unknown;
    }

    public class List1Entry
    {
        public uint NameStringID;
        public uint ParentStringID;
        public List<List1EntryEntry> Entries;
    }


    public class List1EntryEntry
    {
        public uint NameStringID;
        public byte Unknown;
        public uint TypeStringID;
    }

    public class Struct {
        public uint ID;
        public string Name;

        public List<StructField> Fields;
    }

    public class StructField {
        public uint ID;
        public string Name;
        public StructFieldClassification Classification;
        public uint TypeID;
        public uint ParentID;
    }

    public enum StructFieldClassification
    {
        SingleField = 0,
        Map = 1,
        List = 2
    }

    public class DerivedStruct
    {
        public uint ID;
        public string Name;
        public uint Parent;

        public DerivedStructFieldMask[] FieldMasks;
    }

    public class DerivedStructFieldMask
    {
        public uint ID;
        public string Name;
        public DerivedStructFieldMaskType Type;
    }

    public enum DerivedStructFieldMaskType
    {
        KEEP = 0x01,
        REMOVE = 0x02
    }
}
