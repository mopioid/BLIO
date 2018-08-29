using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.RegularExpressions;


public static class BLIO
{
    /// <summary>
    ///  Attempts to connect to the CommandInjector pipe.
    /// </summary>
    /// <returns>
    ///  The connected pipe, or null if it could not be connected to.
    /// </returns>
    /// <exception cref="System.IO.IOException" />
    /// 
    private static NamedPipeClientStream ConnectPipe()
    {
        // Create the connection to the pipe.
        var pipe = new NamedPipeClientStream(".", "BLCommandInjector", PipeDirection.InOut);
        if (!pipe.IsConnected)
            // Attempt to connect to the pipe, timing out after one second.
            // The timeout will occur if no game is running, for example.
            try
            {
                pipe.Connect(1000);
            }
            // If we do timeout, we will be closing it and returning null.
            catch (TimeoutException exception)
            {
#if DEBUG
                Console.WriteLine(exception);
#endif
            }

        // If the pipe was able to be connected, set it to communicate in
        // message mode, and return it.
        if (pipe.IsConnected)
        {
            pipe.ReadMode = PipeTransmissionMode.Message;
            return pipe;
        }
        // If the pipe was not able to connect, close it and return null.
        pipe.Close();
        return null;
    }

    /// <summary>
    ///  Runs a command in the game console.
    /// </summary>
    /// <returns>
    ///  The lines of output from the command. If the command has no output, an
    ///  empty array is returned. If the command could not be run, returns null.
    /// </returns>
    /// <exception cref="System.ArgumentNullException" />
    /// <exception cref="System.FormatException" />
    /// 
    public static IReadOnlyList<string> RunCommand(string format, params object[] arguments)
    {
        string command = String.Format(format, arguments);
        List<string> results = new List<string>();

        // We will attempt to run the command up 3 times.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            // If we can't even connect to the pipe, return null.
            var pipe = ConnectPipe();
            if (pipe == null)
                return null;

            // Create the reading and writing objects for the pipe.
            var pipeWriter = new StreamWriter(pipe);
            var pipeReader = new StreamReader(pipe);

            // While attempting to perform reads and writes, we will be catching
            // any IOExceptions that occur.
            try
            {
                // Write the line to the pipe, and flushing the pipe so that it
                // is transmitted fully.
                pipeWriter.WriteLine(command);
                pipeWriter.Flush();

                // Loop as we read from the pipe.
                for (; ; )
                {
                    string line = pipeReader.ReadLine();
                    // If reading returns null, we've reached the message's end.
                    if (line == null)
                        break;
                    // If there was data to read, add it to our results.
                    if (line != string.Empty)
                        results.Add(line);
                }
                // If we've gotten this far without any exceptions, the query is
                // complete, so return the results.
                return results.AsReadOnly();
            }
            catch (IOException exception)
            {
#if DEBUG
                Console.WriteLine(exception);
#endif
            }
            pipe.Close();
        }
        // If all three attempts encountered an IOException, return null.
        return null;
    }

    // getall results should be in the following format:
    //     <index>) <subclass> <object>.Name = ...
    private static Lazy<Regex> GetallPattern = new Lazy<Regex>(() => new Regex(@"^\d+\) ([^ ]+) ([^']+)\.Name = ", RegexOptions.Compiled));

    /// <summary>
    ///  Performs a getall command for a given class and property, and returns
    ///  a list of all objects of that class.
    /// </summary>
    /// <param name="className">The name of the class to retreive each object for.</param>
    /// <returns>
    ///  A list of objects of the given class.
    /// </returns>
    /// 
    public static IReadOnlyList<BLObject> GetAll(string className)
    {
        var results = new List<BLObject>();

        // Run the getall command. If this fails, return the empty results.
        var output = RunCommand("getall {0} Name", className);
        if (output == null)
            return results;

        // Iterate over the lines of the getall results.
        foreach (string line in output)
        {
            // Attempt to match the line against the expected format. If this
            // fails, skip it.
            var match = GetallPattern.Value.Match(line);
            if (!match.Success)
                continue;

            // Extract the object's name and subclass name from the match.
            string subclassName = match.Groups[1].Value;
            string objectName = match.Groups[2].Value;

            // Create a new object accordingly, and add it to our results.
            results.Add(new BLObject(objectName, subclassName));
        }

        return results.AsReadOnly();
    }


    // Getall array value members should be in the following format:
    //     <class name>'<object name>'
    private static Lazy<Regex> _MemberPattern = new Lazy<Regex>(() => new Regex(@"^\t\d+: (.*)$", RegexOptions.Compiled));


    /// <summary>
    ///  Performs a getall command for a given class and property, and returns
    ///  a dictionary with the property values keyed by their objects.
    /// </summary>
    /// <param name="className">The name of the class to retreive each object for.</param>
    /// <param name="property">The property to retreive for each object.</param>
    /// <returns>
    ///  A dictionary in which objects of the class key their value for the
    ///  specified property. If the property contains a singular value, the
    ///  value is a string. If it contains an array, the value is an
    ///  IReadOnlyList&lt;string&gt;.
    /// </returns>
    /// 
    public static IReadOnlyDictionary<BLObject, object> GetAll(string className, string property)
    {
        // Create the dictionary we will return.
        var results = new Dictionary<BLObject, object>();

        // Run the getall command. If this fails, return the empty results.
        var output = RunCommand("getall {0} {1}", className, property);
        if (output == null)
            return results;

        // getall results should be in the following format:
        //     <index>) <subclass> <object>.<property> = <value>
        // Or, if the result's property is an array:
        //     <index>) <subclass> <object>.<property> =
        Regex objectPattern = new Regex($@"^\d+\) ([^ ]+) (.+)\.{Regex.Escape(property)} =( ?)(.*)$", RegexOptions.Compiled);

        // The current object and array we are working with.
        BLObject objectKey = null;
        List<string> arrayValue = null;

        // Iterate over each line of output.
        foreach (string line in output)
        {
            // The match for if we test a line for an object result.
            Match objectMatch;
            
            // If we are currently working with an array as an object's value,
            // we will test the current line for its membership.
            if (arrayValue != null)
            {
                // Check that the current line is of the format for a member of
                // an array value.
                var memberMatch = _MemberPattern.Value.Match(line);
                if (memberMatch.Success)
                {
                    // If it is, add the captured value to the array, and
                    // proceed on to the next line.
                    arrayValue.Add(memberMatch.Groups[1].Value);
                    continue;
                }

                // If the line is not a member of the array, check whether it is
                // of the format denoting a new object.
                objectMatch = objectPattern.Match(line);
                if (objectMatch.Success)
                {
                    // If it is, this indicates the working array value was
                    // complete, so associate with the working object, and null
                    // it to indicate we're no longer working with an array.
                    results[objectKey] = arrayValue;
                    arrayValue = null;
                }
            }
            else
            {
                // If we are not currently working with an array, check whether
                // the line is of the format denoting a new object. If not, skip
                // this line and proceed to the next one.
                objectMatch = objectPattern.Match(line);
                if (!objectMatch.Success)
                    continue;
            }

            // By now we have a match for an object declaration. Extract the
            // name and class name from it, and create a new object accordingly.
            string subclassName = objectMatch.Groups[1].Value;
            string objectName = objectMatch.Groups[2].Value;
            objectKey = new BLObject(objectName, subclassName);

            // If the value capture group for the match did capture, associate
            // the object with the value.
            if (objectMatch.Groups[3].Value.Length == 1)
                results[objectKey] = objectMatch.Groups[4].Value;

            // Otherwise, we are to expect an array as the value, so create a
            // new list for indicating such and for storing the results.
            else
                arrayValue = new List<string>();
        }

        // At the end of the results, if we had a working array value,
        // associate with the working object.
        if (arrayValue != null)
            results[objectKey] = arrayValue;

        return results;
    }

    /// <summary>
    ///  Represents an object in game.
    /// </summary>
    /// 
    public class BLObject: IEquatable<BLObject>
    {
        /// <summary>The object's name, suitable for set commands, etcetera.</summary>
        /// 
        public readonly string Name;

        /// <summary>The object's class.</summary>
        /// 
        public readonly string Class;

        /// <summary>The manner in which a BLObject's properties should be fetched.</summary>
        public enum PropertyMode
        {
            /// <summary>
            ///  A dump of the object is acquired, and then stored for use each
            ///  time a property of the object is queried. This is faster than
            ///  the GetAll mode if the object does not have very large dumps,
            ///  and several of its properties will be used.
            /// </summary>
            Dump,

            /// <summary>
            ///  Each time a property of the object is accessed, a getall of the
            ///  property is performed for the object's class. This is faster
            ///  than the Dump mode if the object has particularly large dumps,
            ///  or if few properties of the object will be accessed, and if few
            ///  objects exist in the object's class.
            /// </summary>
            GetAll
        }

        /// <summary>The mode to use when accessing the object's properties.</summary>
        public PropertyMode UsePropertyMode = PropertyMode.Dump;


        // Properties for members of the array will be in the format:
        //     <property>=<value>
        // Or:
        //     <property>(<index>)=<value>
        // Or:
        //     <property>[<index>]=<value>
        // Create a pattern to match these formats.
        private static Lazy<Regex> _PropertyPattern = new Lazy<Regex>(() => new Regex(@"^  ([^=\[\]\(\)]+)(\(\d+\)|\[\d+\])?=(.*)$", RegexOptions.Compiled));

        // Lazily computed dictionary containing the object's property dump.
        private Dictionary<string, object> _Dump = null;
        private IReadOnlyDictionary<string, object> Dump
        {
            get
            {
                // If we've previously processed the dump, return it now.
                if (_Dump != null)
                    return _Dump;

                // Create the dictionary we will return.
                _Dump = new Dictionary<string, object>();

                // Run the dump command. If this fails, leave the results empty.
                var propertiesDump = RunCommand("obj dump {0}", Name);
                if (propertiesDump == null)
                    return _Dump;

                // Iterate through the lines of the result.
                foreach (string propertyDump in propertiesDump)
                {
                    // Check that the line is of the format for a property. 
                    var propertyMatch = _PropertyPattern.Value.Match(propertyDump);
                    if (!propertyMatch.Success)
                        continue;

                    // If it is, extract the name and value for the property.
                    string property = propertyMatch.Groups[1].Value;
                    string value = propertyMatch.Groups[3].Value;

                    // If the second group in the pattern matched, this
                    // indicates the property is a member of the array.
                    if (propertyMatch.Groups[2].Success)
                    {
                        List<string> array;

                        // If we already have an array for the property stored
                        // in our dump, retrieve it.
                        if (_Dump.TryGetValue(property, out object arrayValue) && arrayValue is List<string>)
                            array = (List<string>)arrayValue;
                        // Otherwise, create a new array and add it to the dump.
                        else
                        {
                            array = new List<string>();
                            _Dump[property] = array;
                        }
                        // Add the new value to the property's array.
                        array.Add(value);
                    }
                    // If the pattern did not match that of an array member,
                    // simply add it to the dump as-is.
                    else
                        _Dump[property] = value;
                }

                return _Dump;
            }
        }


        /// <summary>Get the raw value for a property of the object.</summary>
        /// <param name="property">The property to retreive.</param>
        /// <returns>
        ///  If the property contains a singular value, returns a string.
        ///  If it contains an array, returns an IReadOnlyList&lt;string&gt;.
        ///  If the property is not found, returns null.
        /// </returns>
        /// 
        public object this[string property]
        {
            get
            {
                // If we use the object's dump to retrieve properties, do so.
                if (UsePropertyMode == PropertyMode.Dump)
                    return Dump.TryGetValue(property, out object value) ? value : null;

                // Otherwise, we are to get the property via a getall.
                else
                {
                    // Get the values for each object in this one's class.
                    var getall = GetAll(Class, property);
                    // Return the value for this object, if any.
                    return getall.TryGetValue(this, out object value) ? value : null;
                }
            }
        }

        /// <summary>Create an object with the specified name and class.</summary>
        /// <param name="objectName">The name of the object.</param>
        /// <param name="className">The class of the object.</param>
        /// 
        public BLObject(string objectName, string className)
        {
            Name = objectName;
            Class = className;
        }

        // Object declarations should be in the following format:
        //     <class name>'<object name>'
        private static Lazy<Regex> _ObjectPattern = new Lazy<Regex>(() => new Regex(@"^([^']+)'([^']+)'$", RegexOptions.Compiled));

        /// <summary>
        ///  Creates a new object from a declaration in the format
        ///  &lt;class&gt;'&lt;object&gt;'
        /// </summary>
        /// <param name="value"></param>
        /// 
        public BLObject(string value)
        {
            // If we were passed a null value, return one.
            if (value == null)
                return;
            
            // Attempt to match the value against the format of an object
            // declaration. If this fails, return null.
            var match = _ObjectPattern.Value.Match(value);
            if (!match.Success)
                return;

            // Extract the object's class and name.
            Class = match.Groups[1].Value;
            Name = match.Groups[2].Value;
        }

        /// <summary>Create an object based on the local player's WillowPlayerController.</summary>
        /// <returns>The WillowPlayerController object.</returns>
        /// 
        public static BLObject GetPlayerController()
        {
            // Querying all LocalPlayer objects for their Actor property should
            // return a single object and its WillowPlayerController.
            var localPlayerControllers = GetAll("LocalPlayer", "Actor");
            // Iterate over said results, although we will be returning after
            // the first one, if any.
            foreach (string actor in localPlayerControllers.Values)
            {
                // Attempt to parse an object from the value. If this fails, or
                // if the object is not a player controller, skip it.
                var controller = new BLObject(actor);
                if (controller == null || controller.Class != "WillowPlayerController")
                    continue;

                // As player controller objects are massive, set this one to use
                // the GetAll property retrieval mode by default.
                controller.UsePropertyMode = PropertyMode.GetAll;
                return controller;
            }
            return null;
        }

        // Two game objects are considered equal if their class and name match.
        public bool Equals(BLObject other) {
            return (Class == other.Class && Name == other.Name);
        }
        // Compute hash values based on the object declaration format.
        public override int GetHashCode() {
            return $"{Class}'{Name}'".GetHashCode();
        }
    }
}
