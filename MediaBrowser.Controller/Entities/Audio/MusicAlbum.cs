﻿using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace MediaBrowser.Controller.Entities.Audio
{
    /// <summary>
    /// Class MusicAlbum
    /// </summary>
    public class MusicAlbum : Folder, IHasAlbumArtist, IHasArtist, IHasMusicGenres, IHasLookupInfo<AlbumInfo>
    {
        public List<Guid> SoundtrackIds { get; set; }

        public MusicAlbum()
        {
            SoundtrackIds = new List<Guid>();
            Artists = new List<string>();
            AlbumArtists = new List<string>();
        }

        [IgnoreDataMember]
        public override bool SupportsAddingToPlaylist
        {
            get { return true; }
        }

        [IgnoreDataMember]
        public MusicArtist MusicArtist
        {
            get
            {
                return Parents.OfType<MusicArtist>().FirstOrDefault();
            }
        }

        [IgnoreDataMember]
        public List<string> AllArtists
        {
            get
            {
                var list = AlbumArtists.ToList();

                list.AddRange(Artists);

                return list;

            }
        }

        [IgnoreDataMember]
        public string AlbumArtist
        {
            get { return AlbumArtists.FirstOrDefault(); }
        }

        public List<string> AlbumArtists { get; set; }

        /// <summary>
        /// Gets the tracks.
        /// </summary>
        /// <value>The tracks.</value>
        public IEnumerable<Audio> Tracks
        {
            get
            {
                return GetRecursiveChildren(i => i is Audio).Cast<Audio>();
            }
        }

        protected override IEnumerable<BaseItem> GetEligibleChildrenForRecursiveChildren(User user)
        {
            return Tracks;
        }

        /// <summary>
        /// Songs will group into us so don't also include us in the index
        /// </summary>
        /// <value><c>true</c> if [include in index]; otherwise, <c>false</c>.</value>
        [IgnoreDataMember]
        public override bool IncludeInIndex
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Override this to true if class should be grouped under a container in indicies
        /// The container class should be defined via IndexContainer
        /// </summary>
        /// <value><c>true</c> if [group in index]; otherwise, <c>false</c>.</value>
        [IgnoreDataMember]
        public override bool GroupInIndex
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// The unknwon artist
        /// </summary>
        private static readonly MusicArtist UnknwonArtist = new MusicArtist { Name = "<Unknown>" };

        /// <summary>
        /// Override this to return the folder that should be used to construct a container
        /// for this item in an index.  GroupInIndex should be true as well.
        /// </summary>
        /// <value>The index container.</value>
        [IgnoreDataMember]
        public override Folder IndexContainer
        {
            get { return Parent as MusicArtist ?? UnknwonArtist; }
        }

        public List<string> Artists { get; set; }

        /// <summary>
        /// Gets the user data key.
        /// </summary>
        /// <returns>System.String.</returns>
        protected override string CreateUserDataKey()
        {
            var id = this.GetProviderId(MetadataProviders.MusicBrainzReleaseGroup);

            if (!string.IsNullOrWhiteSpace(id))
            {
                return "MusicAlbum-MusicBrainzReleaseGroup-" + id;
            }

            id = this.GetProviderId(MetadataProviders.MusicBrainzAlbum);

            if (!string.IsNullOrWhiteSpace(id))
            {
                return "MusicAlbum-Musicbrainz-" + id;
            }

            return base.CreateUserDataKey();
        }

        protected override bool GetBlockUnratedValue(UserPolicy config)
        {
            return config.BlockUnratedItems.Contains(UnratedItem.Music);
        }

        public AlbumInfo GetLookupInfo()
        {
            var id = GetItemLookupInfo<AlbumInfo>();

            id.AlbumArtists = AlbumArtists;

            var artist = Parents.OfType<MusicArtist>().FirstOrDefault();

            if (artist != null)
            {
                id.ArtistProviderIds = artist.ProviderIds;
            }

            id.SongInfos = GetRecursiveChildren(i => i is Audio)
                .Cast<Audio>()
                .Select(i => i.GetLookupInfo())
                .ToList();

            return id;
        }
    }
}
