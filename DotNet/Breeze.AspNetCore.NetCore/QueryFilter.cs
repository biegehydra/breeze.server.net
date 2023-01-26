
using Breeze.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using System.Linq;

namespace Breeze.AspNetCore {

  /// <summary> Attribute to apply the request's query string to the returned IQueryable </summary>
  /// <remarks> Put [BreezeQueryFilter] on a Controller class to apply Breeze query filtering
  /// and execution to each method that returns an IQueryable or IEnumerable.
  /// <para></para>
  /// See <see href="https://breeze.github.io/doc-net/webapi-controller-core#breezequeryfilterattribute"/>
  /// </remarks>
  public class BreezeQueryFilterAttribute : ActionFilterAttribute {

    /// <summary> Check if context.ModelState is valid </summary>
    public override void OnActionExecuting(ActionExecutingContext context) {
      if (!context.ModelState.IsValid) {
        context.Result = new BadRequestObjectResult(context.ModelState);
      }
    }

    /// <summary> Extract the IQueryable from the context, apply the query, and execute it. </summary>
    public override void OnActionExecuted(ActionExecutedContext context) {

      // don't attempt to process queryable if we are throwing an error
      if (context.Result is IStatusCodeActionResult scar && scar.StatusCode >= 400) {
        base.OnActionExecuted(context);
        return;
      }

      var qs = QueryFns.ExtractAndDecodeQueryString(context);
      var queryable = QueryFns.ExtractQueryable(context);

      if (!EntityQuery.NeedsExecution(qs, queryable)) {
        base.OnActionExecuted(context);
        return;
      }

      var eq = new EntityQuery(qs);
      var eleType = TypeFns.GetElementType(queryable.GetType());
      eq.Validate(eleType);

      
      int? inlineCount = null;

      var originalQueryable = queryable;
      queryable = eq.ApplyWhere(queryable, eleType);
      
      if (eq.IsInlineCountEnabled) {
        inlineCount = (int)Queryable.Count((dynamic)queryable);
      }


      queryable = EntityQuery.ApplyCustomLogic(eq, queryable, eleType);
      queryable = eq.ApplyOrderBy(queryable, eleType);
      queryable = eq.ApplySkip(queryable, eleType);
      queryable = eq.ApplyTake(queryable, eleType);
      queryable = eq.ApplySelect(queryable, eleType);
      queryable = EntityQuery.ApplyExpand(eq, queryable, eleType);
      

      if (queryable != originalQueryable) {
        // if a select or expand was encountered we need to
        // execute the DbQueries here, so that any exceptions thrown can be properly returned.
        // if we wait to have the query executed within the serializer, some exceptions will not
        // serialize properly.
        var listResult = Enumerable.ToList((dynamic)queryable);
        listResult = EntityQuery.AfterExecution(eq, queryable, listResult);

        var qr = new QueryResult(listResult, inlineCount);
        context.Result = new ObjectResult(qr);
      }

      base.OnActionExecuted(context);

    }
  }

}

