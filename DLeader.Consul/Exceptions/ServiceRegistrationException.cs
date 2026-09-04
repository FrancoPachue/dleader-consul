namespace DLeader.Consul.Exceptions
{
    /// <summary>
    /// Thrown when registering or deregistering this instance as a Consul service fails.
    /// </summary>
    public class ServiceRegistrationException : ConsulException
    {
        /// <summary>Creates the exception with a message.</summary>
        /// <param name="message">Description of the failure.</param>
        public ServiceRegistrationException(string message) : base(message) { }

        /// <summary>Creates the exception with a message and the underlying cause.</summary>
        /// <param name="message">Description of the failure.</param>
        /// <param name="inner">The exception that caused this one.</param>
        public ServiceRegistrationException(string message, Exception inner) : base(message, inner) { }
    }
}
