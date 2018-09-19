namespace Sitecore.Support.Commerce.Engine.Connect.DataProvider
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using Newtonsoft.Json.Linq;
  using Sitecore;
  using Sitecore.Collections;
  using Sitecore.Commerce.Core;
  using Sitecore.Commerce.Engine.Connect.DataProvider.Definitions;
  using Sitecore.Commerce.Engine.Connect.Fields;
  using Sitecore.Commerce.Engine.Connect.SitecoreDataProvider.Extensions;
  using Sitecore.Commerce.Plugin.Catalog;
  using Sitecore.Data;
  using Sitecore.Data.DataProviders;
  using Sitecore.Data.Items;
  using Sitecore.Data.Managers;
  using Sitecore.Diagnostics;
  using Sitecore.Commerce.Engine.Connect.DataProvider;

  public class CatalogDataProvider : Sitecore.Commerce.Engine.Connect.DataProvider.CatalogDataProvider
  {
    public override FieldList GetItemFields(ItemDefinition item, VersionUri version, CallContext context)
    {
      try
      {
        if (!CanProcessItem(item))
        {
          return null;
        }

        if (item.TemplateID == KnownItemIds.NavigationItemTemplateId)
        {
          return null;
        }

        var template = TemplateManager.GetTemplate(item.TemplateID, ContentDb);
        if (template == null)
        {
          return null;
        }

        context.Abort();

        var repository = new CatalogRepository(version.Language.CultureInfo.Name);

        var combinedEntityId = repository.GetEntityIdFromMappings(item.ID.Guid.ToString());

        // Validate the combinedEntityId
        if (string.IsNullOrEmpty(combinedEntityId))
        {
          Log.Error($"Could not find the combined entity ID for Item ID {item.ID} with template ID {item.TemplateID}", this);
          return null;
        }

        var itemId = item.ID.Guid.ToString();
        var variationId = string.Empty;

        if (item.TemplateID.ToString().Equals(SellableItemVariantTemplateId))
        {
          var parts = combinedEntityId.Split('|');

          itemId = GuidUtils.GetDeterministicGuidString(parts[0]);
          variationId = parts[1];
        }

        var entity = repository.GetEntity(itemId);
        if (entity == null)
        {
          return null;
        }

        var source = entity;
        var tokens = new List<JToken>();

        if (!string.IsNullOrEmpty(variationId))
        {
          var variant =
              entity["Components"]
                  .FirstOrDefault(x => x["@odata.type"].Value<string>().Contains("ItemVariationsComponent"))?["ChildComponents"]
                  .FirstOrDefault(x => x["Id"].Value<string>().Equals(variationId));

          source = variant;
          tokens.Add(variant);
        }

        tokens.Add(entity);

        var fields = new FieldList();
        var templateFields = template.GetFields();

        // Map data fields
        foreach (var field in templateFields.Where(ItemUtil.IsDataField))
        {
          if (field.Name.Equals("VariationProperties"))
          {
            fields.Add(field.ID, repository.GetVariationProperties());
          }
          else if (field.Name.Equals("AreaServed"))
          {
            // TODO: Come up with a better solution to deal with those fields.

            var property = tokens.GetEntityProperty(field.Name);

            var location = property?.ToObject<GeoLocation>();
            if (location != null)
            {
              var parts = new List<string>();

              foreach (var part in new[] { location.City, location.Region, location.PostalCode })
              {
                if (!string.IsNullOrEmpty(part))
                {
                  parts.Add(part);
                }
              }

              fields.Add(field.ID, string.Join(", ", parts));
            }
          }
          else
          {
            var value = tokens.GetEntityValue(field.Name);
            if (value != null)
            {
              fields.Add(field.ID, value);
            }
          }
        }

        var components = source["Components"] ?? source["ChildComponents"];

        // Map external settings
        var externalSettings = components.GetExternalSettings();

        var languageSettings = new Dictionary<string, string>();
        if (externalSettings.ContainsKey(version.Language.Name))
        {
          languageSettings = externalSettings[version.Language.Name];
        }

        var sharedSettings = new Dictionary<string, string>();
        if (externalSettings.ContainsKey("shared"))
        {
          sharedSettings = externalSettings["shared"];
        }

        foreach (var language in new[] { languageSettings, sharedSettings })
        {
          foreach (var setting in language)
          {
            var settingsField = templateFields.FirstOrDefault(x => x.Name.Equals(setting.Key));
            if (settingsField != null)
            {
              fields.Add(settingsField.ID, setting.Value);
            }
          }
        }

        // Map relationships
        var relationships = components?.FirstOrDefault(x => x["@odata.type"].Value<string>().Contains("RelationshipsComponent"));
        if (relationships != null)
        {
          var relationshipsValue = relationships["Relationships"];

          foreach (var token in relationshipsValue)
          {
            var settingsField = templateFields.FirstOrDefault(x => x.Name.Equals(token["Name"].Value<string>()));
            if (settingsField != null)
            {
              fields.Add(settingsField.ID, string.Join("|", token["RelationshipList"].Values<string>()));
            }
          }
        }

        // Map standard fields
        fields.Add(FieldIDs.DisplayName, source["DisplayName"]?.Value<string>());
        fields.Add(FieldIDs.Created, entity.GetEntityValue("DateCreated"));
        fields.Add(FieldIDs.Updated, entity.GetEntityValue("DateUpdated"));

        return fields;
      }
      catch (Exception ex)
      {
        var errorMsg = $"There was an error in GetItemFields. ItemDefinition ID: {item.ID} Template ID: {item.TemplateID}.\r\nError StackTrace: {ex.StackTrace}";
        Log.Error(errorMsg, this);
        return null;
      }
    }
  }
}