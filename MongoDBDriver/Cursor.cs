using System;
using System.Collections.Generic;
using System.IO;
using MongoDB.Driver.Connections;
using MongoDB.Driver.Protocol;
using MongoDB.Driver.Serialization;

namespace MongoDB.Driver
{
    public class Cursor : ICursor
    {
        private readonly Connection _connection;
        private readonly Document _specOpts = new Document();
        private bool _isModifiable = true;
        private Document _spec;
        private Document _fields;
        private int _limit;
        private QueryOptions _options;
        private ReplyMessage<Document> _reply;
        private int _skip;
        private readonly ISerializationFactory _serializationFactory = SerializationFactory.Default;

        /// <summary>
        ///   Initializes a new instance of the <see cref = "Cursor&lt;T&gt;" /> class.
        /// </summary>
        /// <param name = "connection">The conn.</param>
        /// <param name = "fullCollectionName">Full name of the collection.</param>
        public Cursor(Connection connection, string fullCollectionName){
            //Todo: should be internal
            Id = -1;
            _connection = connection;
            FullCollectionName = fullCollectionName;
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref = "Cursor&lt;T&gt;" /> class.
        /// </summary>
        /// <param name = "connection">The conn.</param>
        /// <param name = "fullCollectionName">Full name of the collection.</param>
        [Obsolete("Use Cursor(Connection, fullCollectionName) and then call the Spec, Limit, Skip and Fields methods")]
        public Cursor(Connection connection, string fullCollectionName, Document spec, int limit, int skip, Document fields)
            : this(connection, fullCollectionName){
            //Todo: should be internal
            if(spec == null)
                spec = new Document();
            _spec = spec;
            _limit = limit;
            _skip = skip;
            _fields = fields;
        }

        /// <summary>
        /// Gets or sets the full name of the collection.
        /// </summary>
        /// <value>The full name of the collection.</value>
        public string FullCollectionName { get; private set; }

        /// <summary>
        /// Gets or sets the id.
        /// </summary>
        /// <value>The id.</value>
        public long Id { get; private set; }

        /// <summary>
        /// Specs the specified spec.
        /// </summary>
        /// <param name="spec">The spec.</param>
        /// <returns></returns>
        public ICursor Spec(Document spec){
            TryModify();
            _spec = spec;
            return this;
        }

        /// <summary>
        /// Limits the specified limit.
        /// </summary>
        /// <param name="limit">The limit.</param>
        /// <returns></returns>
        public ICursor Limit(int limit){
            TryModify();
            _limit = limit;
            return this;
        }

        /// <summary>
        /// Skips the specified skip.
        /// </summary>
        /// <param name="skip">The skip.</param>
        /// <returns></returns>
        public ICursor Skip(int skip){
            TryModify();
            _skip = skip;
            return this;
        }

        /// <summary>
        /// Limits the returned documents to the specified fields
        /// </summary>
        /// <param name="fields">The fields.</param>
        /// <returns></returns>
        public ICursor Fields(Document fields){
            TryModify();
            _fields = fields;
            return this;
        }

        /// <summary>
        /// Sorts the specified ascending on the specified field name.
        /// </summary>
        /// <param name = "field">The field.</param>
        /// <returns></returns>
        public ICursor Sort(string field){
            return Sort(field, IndexOrder.Ascending);
        }

        /// <summary>
        /// Sorts on the specified field.
        /// </summary>
        /// <param name = "field">The field.</param>
        /// <param name = "order">The order.</param>
        /// <returns></returns>
        public ICursor Sort(string field, IndexOrder order){
            return Sort(new Document().Add(field, order));
        }

        /// <summary>
        /// Document containing the fields to sort on and the order (ascending/descending)
        /// </summary>
        /// <param name="fields">The fields.</param>
        /// <returns></returns>
        public ICursor Sort(Document fields)
        {
            TryModify();
            AddOrRemoveSpecOpt("$orderby", fields);
            return this;
        }


        /// <summary>
        /// Hint to use the specified index.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <returns></returns>
        public ICursor Hint(Document index)
        {
            TryModify();
            AddOrRemoveSpecOpt("$hint", index);
            return this;
        }

        /// <summary>
        /// Snapshot mode assures that objects which update during the lifetime of a query are returned once 
        /// and only once. This is most important when doing a find-and-update loop that changes the size of 
        /// documents that are returned ($inc does not change size).
        /// </summary>
        /// <param name = "index">The index.</param>
        /// <remarks>Because snapshot mode traverses the _id index, it may not be used with sorting or 
        /// explicit hints. It also cannot use any other index for the query.</remarks>
        public ICursor Snapshot(){
            TryModify();
            AddOrRemoveSpecOpt("$snapshot", true);
            return this;
        }

        /// <summary>
        ///   Explains this instance.
        /// </summary>
        /// <returns></returns>
        public Document Explain(){
            TryModify();
            _specOpts["$explain"] = true;

            var documents = Documents;
            
            using((IDisposable)documents){
                foreach(var document in documents)
                    return document;
            }

            throw new InvalidOperationException("Explain failed.");
        }

        /// <summary>
        ///   Gets a value indicating whether this <see cref = "Cursor&lt;T&gt;" /> is modifiable.
        /// </summary>
        /// <value><c>true</c> if modifiable; otherwise, <c>false</c>.</value>
        public bool IsModifiable{
            get { return _isModifiable; }
        }

        /// <summary>
        ///   Gets the documents.
        /// </summary>
        /// <value>The documents.</value>
        public IEnumerable<Document> Documents{
            get{
                if(_reply == null)
                    RetrieveData();
                if(_reply == null)
                    throw new InvalidOperationException("Expecting reply but get null");

                var documents = _reply.Documents;
                var documentCount = 0;
                var shouldBreak = false;

                while(!shouldBreak){
                    foreach(var document in documents)
                        if((_limit == 0) || (_limit != 0 && documentCount < _limit)){
                            documentCount++;
                            yield return document;
                        }
                        else
                            yield break;

                    if(Id != 0){
                        RetrieveMoreData();
                        documents = _reply.Documents;
                        if(documents == null)
                            shouldBreak = true;
                    }
                    else
                        shouldBreak = true;
                }
            }
        }

        /// <summary>
        ///   Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose(){
            if(Id == 0) //All server side resources disposed of.
                return;

            var killCursorsMessage = new KillCursorsMessage(Id);

            try{
                _connection.SendMessage(killCursorsMessage);
                Id = 0;
            }
            catch(IOException exception){
                throw new MongoCommException("Could not read data, communication failure", _connection, exception);
            }
        }

        /// <summary>
        ///   Optionses the specified options.
        /// </summary>
        /// <param name = "options">The options.</param>
        /// <returns></returns>
        public ICursor Options(QueryOptions options){
            TryModify();
            _options = options;
            return this;
        }

        /// <summary>
        ///   Retrieves the data.
        /// </summary>
        private void RetrieveData(){
            var descriptor = _serializationFactory.GetBsonDescriptor(typeof(Document), _connection);

            var query = new QueryMessage<Document>(descriptor){
                FullCollectionName = FullCollectionName,
                Query = BuildSpec(),
                NumberToReturn = _limit,
                NumberToSkip = _skip,
                Options = _options
            };

            if(_fields != null)
                query.ReturnFieldSelector = _fields;

            var builder = _serializationFactory.GetBsonBuilder(typeof(Document), _connection);

            try{
                _reply = _connection.SendTwoWayMessage<Document>(query, builder);
                Id = _reply.CursorId;
                if(_limit < 0)
                    _limit = _limit*-1;
                _isModifiable = false;
            }
            catch(IOException exception){
                throw new MongoCommException("Could not read data, communication failure", _connection, exception);
            }
        }

        /// <summary>
        ///   Retrieves the more data.
        /// </summary>
        private void RetrieveMoreData(){
            var getMoreMessage = new GetMoreMessage(FullCollectionName, Id, _limit);

            var builder = _serializationFactory.GetBsonBuilder(typeof(Document), _connection);

            try{
                _reply = _connection.SendTwoWayMessage<Document>(getMoreMessage, builder);
                Id = _reply.CursorId;
            }
            catch(IOException exception){
                Id = 0;
                throw new MongoCommException("Could not read data, communication failure", _connection, exception);
            }
        }

        /// <summary>
        ///   Tries the modify.
        /// </summary>
        private void TryModify(){
            if(_isModifiable)
                return;
            throw new InvalidOperationException("Cannot modify a cursor that has already returned documents.");
        }

        /// <summary>
        ///   Adds the or remove spec opt.
        /// </summary>
        /// <param name = "key">The key.</param>
        /// <param name = "doc">The doc.</param>
        private void AddOrRemoveSpecOpt(string key, object doc){
            if(doc == null)
                _specOpts.Remove(key);
            else
                _specOpts[key] = doc;
        }

        /// <summary>
        ///   Builds the spec.
        /// </summary>
        /// <returns></returns>
        private object BuildSpec(){
            if(_specOpts.Count == 0)
                return _spec;

            var document = new Document();
            _specOpts.CopyTo(document);
            document["$query"] = _spec;
            return document;
        }
    }
}