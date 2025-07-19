using System;

namespace Service
{
    public class AudioStreamNotFoundException : Exception
    {
        public AudioStreamNotFoundException() { }
        public AudioStreamNotFoundException(string message) : base(message) { }
        public AudioStreamNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class VideoStreamNotFoundException : Exception
    {
        public VideoStreamNotFoundException() { }
        public VideoStreamNotFoundException(string message) : base(message) { }
        public VideoStreamNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }
}