using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLeader.Consul.Exceptions
{
    /// <summary>
    /// Exception thrown when Consul operations fail
    /// </summary>
    public class ConsulException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the ConsulException class
        /// </summary>
        public ConsulException() { }

        /// <summary>
        /// Initializes a new instance of the ConsulException class with a message
        /// </summary>
        /// <param name="message">The error message</param>
        public ConsulException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the ConsulException class with a message and inner exception
        /// </summary>
        /// <param name="message">The error message</param>
        /// <param name="innerException">The inner exception</param>
        public ConsulException(string message, Exception innerException) : base(message, innerException) { }
    }
}
