namespace DLeader.Consul.Exceptions
{
    /// <summary>
    /// Thrown when a Consul session cannot be created, renewed or destroyed.
    /// </summary>
    public class SessionException : ConsulException
    {
        /// <summary>Creates the exception with a message.</summary>
        /// <param name="message">Description of the failure.</param>
        public SessionException(string message) : base(message) { }

        /// <summary>Creates the exception with a message and the underlying cause.</summary>
        /// <param name="message">Description of the failure.</param>
        /// <param name="inner">The exception that caused this one.</param>
        public SessionException(string message, Exception inner) : base(message, inner) { }
    }
}
