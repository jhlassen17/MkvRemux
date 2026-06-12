using System;

namespace MkvRemux
{
    /// <summary>
    /// Descriptor that describes a title and its episode information (if applicable)
    /// </summary>
    public class TitleDescriptor
    {

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="TitleDescriptor"/> class.
        /// </summary>
        protected TitleDescriptor()
        {
            // Protected default constructor for use by derived classes or factory methods
            this.Title = string.Empty;
            this.Episode = string.Empty;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TitleDescriptor"/> class.
        /// </summary>
        /// <param name="title">The title.</param>
        /// <param name="episode">The episode.</param>
        public TitleDescriptor(string title, string episode) : this()
        {
            // Save the info
            this.Title = title ?? string.Empty;
            this.Episode = episode ?? string.Empty;
        }

        #endregion

        #region Public Properties

        /// <summary>
        /// Gets the title.
        /// </summary>
        public string Title { get; protected set; }

        /// <summary>
        /// Gets the episode.
        /// </summary>
        public string Episode { get; protected set; }

        /// <summary>
        /// Gets a flag indicating whether this item is a TV show.
        /// </summary>
        public bool IsTVShow => !string.IsNullOrEmpty(this.Episode);

        /// <summary>
        /// Gets a flag indicating whether this is a movie.
        /// </summary>
        public bool IsMovie => string.IsNullOrEmpty(this.Episode);

        /// <summary>
        /// Season number parsed from the Episode string.
        /// </summary>
        public string? Season => this.Episode?.IndexOf('S') >= 0 ? this.Episode.Substring(this.Episode.IndexOf('S') + 1, 2)
            : null;

        /// <summary>
        /// Episode number extracted from the Episode string after the 'E' character.
        /// </summary>
        public string? EpisodeNumber => this.Episode?.IndexOf('E') >= 0 ? 
            this.Episode[(this.Episode.IndexOf('E') + 1)..]
            : null;

        /// <summary>
        /// Gets an empty <see cref="TitleDescriptor"/>.
        /// </summary>
        public static TitleDescriptor Empty => new(string.Empty, string.Empty);

        #endregion
    }
}
