﻿using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Querying;
using ServiceStack;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaBrowser.Api.Music
{
    [Route("/Albums/{Id}/Similar", "GET", Summary = "Finds albums similar to a given album.")]
    public class GetSimilarAlbums : BaseGetSimilarItemsFromItem
    {
    }

    [Route("/Artists/{Id}/Similar", "GET", Summary = "Finds albums similar to a given album.")]
    public class GetSimilarArtists : BaseGetSimilarItemsFromItem
    {
    }

    [Authenticated]
    public class AlbumsService : BaseApiService
    {
        /// <summary>
        /// The _user manager
        /// </summary>
        private readonly IUserManager _userManager;

        /// <summary>
        /// The _user data repository
        /// </summary>
        private readonly IUserDataManager _userDataRepository;
        /// <summary>
        /// The _library manager
        /// </summary>
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepo;
        private readonly IDtoService _dtoService;

        public AlbumsService(IUserManager userManager, IUserDataManager userDataRepository, ILibraryManager libraryManager, IItemRepository itemRepo, IDtoService dtoService)
        {
            _userManager = userManager;
            _userDataRepository = userDataRepository;
            _libraryManager = libraryManager;
            _itemRepo = itemRepo;
            _dtoService = dtoService;
        }

        public object Get(GetSimilarArtists request)
        {
            var result = GetSimilarItemsResult(

                request, 

                SimilarItemsHelper.GetSimiliarityScore);

            return ToOptimizedSerializedResultUsingCache(result);
        }
        
        /// <summary>
        /// Gets the specified request.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns>System.Object.</returns>
        public object Get(GetSimilarAlbums request)
        {
            var dtoOptions = GetDtoOptions(request);

            var result = SimilarItemsHelper.GetSimilarItemsResult(dtoOptions, _userManager,
                _itemRepo,
                _libraryManager,
                _userDataRepository,
                _dtoService,
                Logger,
                request, item => item is MusicAlbum,
                GetAlbumSimilarityScore);

            return ToOptimizedSerializedResultUsingCache(result);
        }

        private ItemsResult GetSimilarItemsResult(BaseGetSimilarItemsFromItem request, Func<BaseItem, List<PersonInfo>, List<PersonInfo>, BaseItem, int> getSimilarityScore)
        {
            var user = !string.IsNullOrWhiteSpace(request.UserId) ? _userManager.GetUserById(request.UserId) : null;

            var item = string.IsNullOrEmpty(request.Id) ?
                (!string.IsNullOrWhiteSpace(request.UserId) ? user.RootFolder :
                _libraryManager.RootFolder) : _libraryManager.GetItemById(request.Id);

            var inputItems = _libraryManager.GetArtists(user.RootFolder.GetRecursiveChildren(user, i => i is IHasArtist).OfType<IHasArtist>());

            var list = inputItems.ToList();

            var items = SimilarItemsHelper.GetSimilaritems(item, _libraryManager, list, getSimilarityScore).ToList();

            IEnumerable<BaseItem> returnItems = items;

            if (request.Limit.HasValue)
            {
                returnItems = returnItems.Take(request.Limit.Value);
            }

            var dtoOptions = GetDtoOptions(request);

            var result = new ItemsResult
            {
                Items = _dtoService.GetBaseItemDtos(returnItems, dtoOptions, user).ToArray(),

                TotalRecordCount = items.Count
            };

            return result;
        }
        
        /// <summary>
        /// Gets the album similarity score.
        /// </summary>
        /// <param name="item1">The item1.</param>
        /// <param name="item1People">The item1 people.</param>
        /// <param name="allPeople">All people.</param>
        /// <param name="item2">The item2.</param>
        /// <returns>System.Int32.</returns>
        private int GetAlbumSimilarityScore(BaseItem item1, List<PersonInfo> item1People, List<PersonInfo> allPeople, BaseItem item2)
        {
            var points = SimilarItemsHelper.GetSimiliarityScore(item1, item1People, allPeople, item2);

            var album1 = (MusicAlbum)item1;
            var album2 = (MusicAlbum)item2;

            var artists1 = album1
                .AllArtists
                .DistinctNames()
                .ToList();

            var artists2 = album2
                .AllArtists
                .DistinctNames()
                .ToDictionary(i => i, StringComparer.OrdinalIgnoreCase);

            return points + artists1.Where(artists2.ContainsKey).Sum(i => 5);
        }
    }
}
