﻿//  Copyright 2011 Marc Fletcher, Matthew Dean
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.ComponentModel.Composition;
using System.IO;

namespace SerializerBase
{    
    [InheritedExport(typeof(Serializer))]
    public abstract class Serializer
    {
        protected static T GetInstance<T>() where T : Serializer
        {
            //this forces helper static constructor to be called
            var instance = ProcessorManager.Instance.GetSerializer<T>() as T;

            if (instance == null)
            {
                //if the instance is null the type was not added as part of composition
                //create a new instance of T and add it to helper as a serializer

                instance = typeof(T).GetConstructor(new Type[] { }).Invoke(new object[] { }) as T;
                ProcessorManager.Instance.AddSerializer(instance);
            }

            return instance;
        }
                        
        /// <summary>
        /// Converts objectToSerialize to an array of bytes using the compression provided by compressor
        /// </summary>
        /// <typeparam name="T">Type of object to serialize</typeparam>
        /// <param name="objectToSerialise">Object to serialize</param>
        /// <param name="dataProcessors">Data processors to apply to serialised data.  These will be run in index order i.e. low index to high</param>
        /// <param name="options">Options dictionary for serialisation/data processing</param>
        /// <returns>Serialized array of bytes</returns>
        public byte[] SerialiseDataObject<T>(T objectToSerialise, List<DataProcessor> dataProcessors, Dictionary<string, string> options)
        {
            //Check to see if the array serializer returns anything
            var baseRes = ArraySerializer.SerialiseArrayObject(objectToSerialise, dataProcessors, options);

            //if the object was an array baseres will != null
            if (baseRes != null)
                return baseRes;

            //Create the first memory stream that will be used 
            using (MemoryStream tempStream1 = new MemoryStream())
            {
                //Serialise the object using the overriden method
                SerialiseDataObjectInt(tempStream1, objectToSerialise, options);

                //If we have no data processing to do we can simply return the serialised bytes
                if (dataProcessors == null || dataProcessors.Count == 0)
                    return tempStream1.ToArray();
                else
                {
                    //Otherwise we will need a second memory stream to process the data
                    using (MemoryStream tempStream2 = new MemoryStream())
                    {
                        //variable will store the number of bytes in the output stream at each processing stage
                        long writtenBytes;
                        //Process the serialised data using the first data processer.  We do this seperately to avoid multiple seek/setLength calls for
                        //the most common usage case
                        dataProcessors[0].ForwardProcessDataStream(tempStream1, tempStream2, options, out writtenBytes);

                        //If we have more than one processor we need to loop through them
                        if (dataProcessors.Count > 1)
                        {
                            //Loop through the remaining processors two at a time.  Each loop processes data temp2 -> temp1 -> temp2
                            for (int i = 1; i < dataProcessors.Count; i += 2)
                            {
                                //Seek streams to zero and truncate the last output stream to the data size
                                tempStream2.Seek(0, 0); tempStream2.SetLength(writtenBytes);
                                tempStream1.Seek(0, 0);
                                //Process the data
                                dataProcessors[i].ForwardProcessDataStream(tempStream2, tempStream1, options, out writtenBytes);

                                //if the second of the pair exists
                                if (i + 1 < dataProcessors.Count)
                                {
                                    //Seek streams to zero and truncate the last output stream to the data size
                                    tempStream2.Seek(0, 0);
                                    tempStream1.Seek(0, 0); tempStream1.SetLength(writtenBytes);
                                    //Process the data
                                    dataProcessors[i + 1].ForwardProcessDataStream(tempStream1, tempStream2, options, out writtenBytes);
                                }
                            }
                        }

                        //Depending on whether the number of processors is even or odd a different stream will hold the final data
                        if (dataProcessors.Count % 2 == 0)
                        {
                            //Seek to the begining and truncate the output stream
                            tempStream1.Seek(0, 0);
                            tempStream1.SetLength(writtenBytes);
                            //Return the resultant bytes
                            return tempStream1.ToArray();
                        }
                        else
                        {
                            //Seek to the begining and truncate the output stream
                            tempStream2.Seek(0, 0);
                            tempStream2.SetLength(writtenBytes);
                            //Return the resultant bytes
                            return tempStream2.ToArray();
                        }
                    }                    
                }
            }            
        }

        /// <summary>
        /// Converts array of bytes previously serialized and compressed using compressor to an object of provided type
        /// </summary>
        /// <typeparam name="T">Type of object to deserialize to</typeparam>
        /// <param name="receivedObjectBytes">Byte array containing serialized and compressed object</param>
        /// <param name="dataProcessors">Data processors to apply to serialised data.  These will be run in reverse order i.e. high index to low</param>
        /// <param name="options">Options dictionary for serialisation/data processing</param>
        /// <returns>The deserialized object</returns>
        public T DeserialiseDataObject<T>(byte[] receivedObjectBytes, List<DataProcessor> dataProcessors, Dictionary<string, string> options)
        {
            //Try to deserialise using the array helper.  If the result is a primitive array this call will return an object
            var baseRes = ArraySerializer.DeserialiseArrayObject(receivedObjectBytes, typeof(T), dataProcessors, options);

            if (baseRes != null)
                return (T)baseRes;

            //Create a memory stream using the incoming bytes as the initial buffer
            using (MemoryStream inputStream = new MemoryStream(receivedObjectBytes))
            {
                //If no data processing is required then we can just deserialise the object straight
                if (dataProcessors == null || dataProcessors.Count == 0)
                    return (T)DeserialiseDataObjectInt(inputStream, typeof(T), options);
                else
                {
                    //Otherwise we will need another stream
                    using (MemoryStream tempStream = new MemoryStream())
                    {
                        //variable will store the number of bytes in the output stream at each processing stage
                        long writtenBytes;
                        //Data processing for deserialization is done in reverse so run the last element
                        dataProcessors[dataProcessors.Count - 1].ReverseProcessDataStream(inputStream, tempStream, options, out writtenBytes);

                        //If we have more than 1 processor we will now run the remaining processors pair wise
                        if (dataProcessors.Count > 1)
                        {
                            //Data processing for deserialization is done in reverse so run from a high index down in steps of 2. Each loop processes data temp -> input -> temp
                            for (int i = dataProcessors.Count - 2; i >= 0; i -= 2)
                            {
                                //Seek streams to zero and truncate the last output stream to the data size
                                inputStream.Seek(0, 0);
                                tempStream.Seek(0, 0); tempStream.SetLength(writtenBytes);
                                //Process the data
                                dataProcessors[i].ReverseProcessDataStream(tempStream, inputStream, options, out writtenBytes);

                                //if the second processor exists run it
                                if (i - 1 >= 0)
                                {
                                    //Seek streams to zero and truncate the last output stream to the data size
                                    inputStream.Seek(0, 0); inputStream.SetLength(writtenBytes);
                                    tempStream.Seek(0, 0);
                                    //Process the data
                                    dataProcessors[i].ReverseProcessDataStream(inputStream, tempStream, options, out writtenBytes);
                                }
                            }
                        }

                        //Depending on whether the number of processors is even or odd a different stream will hold the final data
                        if (dataProcessors.Count % 2 == 0)
                        {
                            //Seek to the begining and truncate the output stream
                            inputStream.Seek(0, 0);
                            inputStream.SetLength(writtenBytes);
                            //Return the resultant bytes
                            return (T)DeserialiseDataObjectInt(inputStream, typeof(T), options);
                        }
                        else
                        {
                            //Seek to the begining and truncate the output stream
                            tempStream.Seek(0, 0);
                            tempStream.SetLength(writtenBytes);
                            //Return the resultant bytes
                            return (T)DeserialiseDataObjectInt(tempStream, typeof(T), options);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Returns a unique identifier for the serializer type.  Used in automatic serialization/compression detection
        /// </summary>
        public abstract byte Identifier { get; }
        
        /// <summary>
        /// Serialises an object to a stream using any relavent options provided
        /// </summary>
        /// <param name="ouputStream">The stream to serialise to</param>
        /// <param name="objectToSerialise">The object to serialise</param>
        /// <param name="options">Options dictionary for serialisation/data processing</param>
        protected abstract void SerialiseDataObjectInt(Stream ouputStream, object objectToSerialise, Dictionary<string, string> options);

        /// <summary>
        /// Deserialises the data in a stream to an object of the spcified type using any relavent provided options 
        /// </summary>
        /// <param name="inputStream">The stream containing the serialised object</param>
        /// <param name="resultType">The return object Type</param>
        /// <param name="options">Options dictionary for serialisation/data processing</param>
        /// <returns>The deserialised object</returns>
        protected abstract object DeserialiseDataObjectInt(Stream inputStream, Type resultType, Dictionary<string, string> options);
    }

    /// <summary>
    /// Abstract class that provides fastest method for serializing arrays of primitive data types.
    /// </summary>
    static class ArraySerializer
    {
        /// <summary>
        /// Serializes objectToSerialize to a byte array using compression provided by compressor if T is an array of primitives.  Otherwise returns default value for T.  Override 
        /// to serialize other types
        /// </summary>
        /// <typeparam name="T">Type paramter of objectToSerialize.  If it is an Array will be serialized here</typeparam>
        /// <param name="objectToSerialise">Object to serialize</param>
        /// <param name="dataProcessors">The compression provider to use</param>
        /// <returns>The serialized and compressed bytes of objectToSerialize</returns>
        public static unsafe byte[] SerialiseArrayObject(object objectToSerialise, List<DataProcessor> dataProcessors, Dictionary<string, string> options)
        {
            Type objType = objectToSerialise.GetType();

            if (objType.IsArray)
            {
                var elementType = objType.GetElementType();

                //No need to do anything for a byte array
                if (elementType == typeof(byte) && (dataProcessors == null || dataProcessors.Count == 0))
                    return objectToSerialise as byte[];
                else if (elementType.IsPrimitive)
                {                                        
                    var asArray = objectToSerialise as Array;
                    GCHandle arrayHandle = GCHandle.Alloc(asArray, GCHandleType.Pinned);

                    try
                    {
                        IntPtr safePtr = Marshal.UnsafeAddrOfPinnedArrayElement(asArray, 0);
                        long writtenBytes = 0; 

                        using (MemoryStream tempStream1 = new System.IO.MemoryStream())
                        {                            
                            using (UnmanagedMemoryStream inputDataStream = new System.IO.UnmanagedMemoryStream((byte*)safePtr, asArray.Length * Marshal.SizeOf(elementType)))
                            {
                                if (dataProcessors == null || dataProcessors.Count == 0)
                                {
                                    inputDataStream.CopyTo(tempStream1);
                                    return tempStream1.ToArray();
                                }

                                dataProcessors[0].ForwardProcessDataStream(inputDataStream, tempStream1, options, out writtenBytes);
                            }

                            if (dataProcessors.Count > 1)
                            {
                                using (MemoryStream tempStream2 = new MemoryStream())
                                {
                                    for (int i = 1; i < dataProcessors.Count; i += 2)
                                    {
                                        tempStream1.Seek(0, 0); tempStream1.SetLength(writtenBytes);
                                        tempStream2.Seek(0, 0);
                                        dataProcessors[i].ForwardProcessDataStream(tempStream1, tempStream2, options, out writtenBytes);

                                        if (i + 1 < dataProcessors.Count)
                                        {
                                            tempStream1.Seek(0, 0);
                                            tempStream2.Seek(0, 0); tempStream2.SetLength(writtenBytes);
                                            dataProcessors[i].ForwardProcessDataStream(tempStream2, tempStream1, options, out writtenBytes);
                                        }
                                    }

                                    if (dataProcessors.Count % 2 == 0)
                                    {
                                        tempStream2.SetLength(writtenBytes + 8);
                                        tempStream2.Seek(writtenBytes, 0);
                                        tempStream2.Write(BitConverter.GetBytes(asArray.Length), 0, sizeof(int));
                                        return tempStream2.ToArray();
                                    }
                                    else
                                    {
                                        tempStream1.SetLength(writtenBytes + 8);
                                        tempStream1.Seek(writtenBytes, 0);
                                        tempStream1.Write(BitConverter.GetBytes(asArray.Length), 0, sizeof(int));
                                        return tempStream1.ToArray();
                                    }
                                }
                            }
                            else
                            {
                                tempStream1.SetLength(writtenBytes + 8);
                                tempStream1.Seek(writtenBytes, 0);
                                tempStream1.Write(BitConverter.GetBytes(asArray.Length), 0, sizeof(int));
                                return tempStream1.ToArray();
                            }
                        }
                    }
                    finally
                    {
                        arrayHandle.Free();
                    }                   
                }
            }

            return null;
        }

        /// <summary>
        /// Deserializes data object held as compressed bytes in receivedObjectBytes using compressor if desired type is an array of primitives
        /// </summary>
        /// <typeparam name="T">Type parameter of the resultant object</typeparam>
        /// <param name="receivedObjectBytes">Byte array containing serialized and compressed object</param>
        /// <param name="dataProcessors">Compression provider to use</param>
        /// <returns>The deserialized object if it is an array, otherwise null</returns>
        public static unsafe object DeserialiseArrayObject(byte[] receivedObjectBytes, Type objType, List<DataProcessor> dataProcessors, Dictionary<string, string> options)
        {
            if (objType.IsArray)
            {
                var elementType = objType.GetElementType();

                //No need to do anything for a byte array
                if (elementType == typeof(byte) && (dataProcessors == null || dataProcessors.Count == 0))
                    return (object)receivedObjectBytes;
                if (elementType.IsPrimitive)
                {
                    int numElements = (int)(BitConverter.ToUInt32(receivedObjectBytes, receivedObjectBytes.Length - sizeof(int)));

                    Array resultArray = Array.CreateInstance(elementType, numElements);

                    GCHandle arrayHandle = GCHandle.Alloc(resultArray, GCHandleType.Pinned);

                    try
                    {
                        IntPtr safePtr = Marshal.UnsafeAddrOfPinnedArrayElement(resultArray, 0);
                        long writtenBytes = 0;

                        using (System.IO.UnmanagedMemoryStream finalOutputStream = new System.IO.UnmanagedMemoryStream((byte*)safePtr, resultArray.Length * Marshal.SizeOf(elementType), resultArray.Length * Marshal.SizeOf(elementType), System.IO.FileAccess.ReadWrite))
                        {
                            using (MemoryStream inputBytesStream = new MemoryStream(receivedObjectBytes, 0, receivedObjectBytes.Length - sizeof(int)))
                            {
                                if (dataProcessors != null && dataProcessors.Count > 1)
                                {
                                    using (MemoryStream tempStream1 = new MemoryStream())
                                    {
                                        dataProcessors[dataProcessors.Count - 1].ReverseProcessDataStream(inputBytesStream, tempStream1, options, out writtenBytes);

                                        if (dataProcessors.Count > 2)
                                        {
                                            using (MemoryStream tempStream2 = new MemoryStream())
                                            {
                                                for (int i = dataProcessors.Count - 2; i > 0; i -= 2)
                                                {
                                                    tempStream1.Seek(0, 0); tempStream1.SetLength(writtenBytes);
                                                    tempStream2.Seek(0, 0);
                                                    dataProcessors[i].ReverseProcessDataStream(tempStream1, tempStream2, options, out writtenBytes);

                                                    if (i - 1 > 0)
                                                    {
                                                        tempStream1.Seek(0, 0);
                                                        tempStream2.Seek(0, 0); tempStream2.SetLength(writtenBytes);
                                                        dataProcessors[i - 1].ReverseProcessDataStream(tempStream2, tempStream1, options, out writtenBytes);
                                                    }
                                                }

                                                if (dataProcessors.Count % 2 == 0)
                                                {
                                                    tempStream1.Seek(0, 0); tempStream1.SetLength(writtenBytes);
                                                    dataProcessors[0].ReverseProcessDataStream(tempStream1, finalOutputStream, options, out writtenBytes);
                                                }
                                                else
                                                {
                                                    tempStream2.Seek(0, 0); tempStream2.SetLength(writtenBytes);
                                                    dataProcessors[0].ReverseProcessDataStream(tempStream2, finalOutputStream, options, out writtenBytes);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            tempStream1.Seek(0, 0); tempStream1.SetLength(writtenBytes);
                                            dataProcessors[0].ReverseProcessDataStream(tempStream1, finalOutputStream, options, out writtenBytes);
                                        }
                                    }
                                }
                                else
                                {
                                    if (dataProcessors != null && dataProcessors.Count == 1)
                                        dataProcessors[0].ReverseProcessDataStream(inputBytesStream, finalOutputStream, options, out writtenBytes);
                                    else
                                        inputBytesStream.CopyTo(finalOutputStream);
                                }
                            }
                        }
                    }
                    finally
                    {
                        arrayHandle.Free();
                    }

                    return (object)resultArray;
                }
            }

            return null;
        }

    }
}
