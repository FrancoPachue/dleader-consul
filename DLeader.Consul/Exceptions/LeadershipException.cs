using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLeader.Consul.Exceptions
{

    /// <summary>
    /// Exception thrown when leadership operations fail
    /// </summary>
    public class LeadershipException : ConsulException
    {
        /// <summary>
        /// Initializes a new instance of the LeadershipException class
        /// </summary>
        public LeadershipException() { }

        /// <summary>
        /// Initializes a new instance of the LeadershipException class with a message
        /// </summary>
        /// <param name="message">The error message</param>
        public LeadershipException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the LeadershipException class with a message and inner exception
        /// </summary>
        /// <param name="message">The error message</param>
        /// <param name="innerException">The inner exception</param>
        public LeadershipException(string message, Exception innerException) : base(message, innerException) { }
    }
}
