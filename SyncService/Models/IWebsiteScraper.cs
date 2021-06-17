﻿using Commons;
using Commons.Collections;
using Commons.Enums;
using Commons.Filters;
using FuzzySharp;
using PuppeteerSharp;
using SyncService.Helpers;
using SyncService.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Models
{
    public abstract class IWebsiteScraper
    {
        private AnimeCollection _animeCollection = new AnimeCollection();
        private EpisodeCollection _episodeCollection = new EpisodeCollection();
        private AnimeSuggestionCollection _animeSuggestionCollection = new AnimeSuggestionCollection();
        private Anime _anime;

        protected abstract long WebsiteID { get; }
        protected Website Website { get; private set; }
        protected abstract Type WebsiteType { get; }
        protected WebsiteScraperService Service { get; set; }
        protected Thread Thread { get; private set; }
        public bool Working { get; private set; }

        public IWebsiteScraper(WebsiteScraperService service)
        {
            this.Service = service;
            this.Website = new WebsiteCollection().Get(this.WebsiteID);
        }

        public void Start()
        {
            this.Thread = new Thread(new ThreadStart(run));
            this.Thread.IsBackground = true;

            this.Thread.Start();
            this.Working = true;
        }

        protected bool AnalyzeMatching(AnimeMatching matching, string sourceTitle)
        {
            matching.Score = Fuzz.TokenSortRatio(matching.Title, sourceTitle);

            if(matching.Score == 100)
            {
                return true;
            }
            else if(matching.Score >= 60)
            {
                var query = this._animeSuggestionCollection.GetList(new AnimeSuggestionFilter()
                {
                    anime_id = _anime.Id,
                    title = matching.Title,
                    source = this.Website.Name
                });

                if(query.Count > 0)
                {
                    if(query.Documents[0].Status == AnimeSuggestionStatusEnum.OK)
                    {
                        return true;
                    }
                    else if(query.Documents[0].Status == AnimeSuggestionStatusEnum.KO)
                    {
                        return false;
                    }
                }
                else
                {
                    AnimeSuggestion suggestion = new AnimeSuggestion()
                    {
                        AnimeID = _anime.Id,
                        Title = matching.Title,
                        Source = this.Website.Name,
                        Score = matching.Score,
                        Path = $"{this.Website.SiteUrl}{matching.Path}",
                        Status = AnimeSuggestionStatusEnum.NONE
                    };

                    this._animeSuggestionCollection.Add(ref suggestion);
                }
            }

            return false;
        }

        protected abstract Task<AnimeMatching> GetMatching(Page webPage, string animeTitle);
        protected abstract Task<EpisodeMatching> GetEpisode(Page webPage, AnimeMatching matching, int number);

        private async void run()
        {
            string browserKey = ProxyHelper.Instance.GenerateBrowserKey(this.WebsiteType);

            try
            {
                long lastID = this._animeCollection.Last().Id;

                for (int animeID = 1; animeID < lastID; animeID++)
                {
                    try
                    {
                        _anime = this._animeCollection.Get(animeID);
                        this.Service.Log($"Website {this.Website.Name} doing {_anime.Titles[LocalizationEnum.English]}");

                        if (!this.animeNeedWork())
                        {
                            throw new Exception();
                        }

                        AnimeMatching matching = null;
                        using (Page webPage = await ProxyHelper.Instance.GetBestProxy(browserKey, this.Website.CanBlockRequests))
                        {
                            string animeTitle = _anime.Titles.ContainsKey(this.Website.Localization) ?
                                _anime.Titles[this.Website.Localization] :
                                _anime.Titles[LocalizationEnum.English];

                            matching = await this.GetMatching(webPage, animeTitle);
                        }

                        if (matching == null)
                        {
                            this.Service.Log($"Website {this.Website.Name} not found {_anime.Titles[LocalizationEnum.English]}");
                            throw new Exception();
                        }
                        
                        try
                        {
                            for (int i = 1; i <= _anime.EpisodesCount; i++)
                            {
                                using (Page webPage = await ProxyHelper.Instance.GetBestProxy(browserKey, this.Website.CanBlockRequests))
                                {
                                    EpisodeMatching episode = await this.GetEpisode(webPage, matching, i);

                                    if (episode != null)
                                    {
                                        matching.Episodes.Add(episode);
                                    }
                                }
                            }
                        }
                        catch 
                        {
                            this.Service.Log($"Website {this.Website.Name} no episode found ({_anime.Titles[LocalizationEnum.English]})");
                        }

                        if (this.Website.Official)
                        {
                            _anime.Titles[this.Website.Localization] = matching.Title;
                            _anime.Descriptions[this.Website.Localization] = matching.Description;

                            this._animeCollection.Edit(ref _anime);
                        }

                        if (matching.Episodes.Count > 0)
                        {
                            foreach (EpisodeMatching episode in matching.Episodes)
                            {
                                Episode ep = new Episode()
                                {
                                    AnimeID = _anime.Id,
                                    Source = this.Website.Name,
                                    Number = episode.Number,
                                    Title = episode.Title,
                                    Video = episode.Source,
                                    Locale = this.Website.Localization
                                };

                                if (this._episodeCollection.Exists(ref ep))
                                {
                                    this._episodeCollection.Edit(ref ep);
                                }
                                else
                                {
                                    this._episodeCollection.Add(ref ep);
                                }
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        this.Service.Log($"Website {this.Website.Name} done {this.Service.GetProgressD(animeID, lastID)}% ({_anime.Titles[LocalizationEnum.English]})");
                    }
                }
            }
            catch(Exception ex)
            {
                this.Service.Log($"Error: {ex.Message}");
            }
            finally
            {
                ProxyHelper.Instance.CloseProxy(browserKey);
                this.Working = false;
            }
        }

        private bool animeNeedWork()
        {
            if(_anime.Status == AnimeStatusEnum.RELEASING)
            {
                return true;
            }

            long episodesCount = this._episodeCollection.GetList<EpisodeFilter>(new EpisodeFilter()
            {
                anime_id = _anime.Id,
                source = this.Website.Name
            }).Count;

            if (episodesCount == 0 || (_anime.Status == AnimeStatusEnum.FINISHED && _anime.EpisodesCount != episodesCount))
            {
                return true;
            }

            return false;
        }
    }
}
