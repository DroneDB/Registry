using System;
using System.Collections.Generic;
using System.Text;

namespace Registry.Common;

public class DictionaryEx<TKey, TValue> : Dictionary<TKey, TValue>
{

    public new void Add(TKey key, TValue value)
    {
        try
        {
            base.Add(key, value);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"Cannot add duplicate key '{key}' with value '{value}' in dictionary <{typeof(TKey).Name}, {typeof(TValue).Name}>", ex);
        }
    }

    public new TValue this[TKey key]
    {
        get
        {
            try
            {
                return base[key];
            }
            catch (KeyNotFoundException ex)
            {
                throw new KeyNotFoundException(
                    $"Cannot find key '{key}' in dictionary <{typeof(TKey).Name}, {typeof(TValue).Name}>", ex);
            }
        }
        set
        {

            try
            {
                base[key] = value;
            }
            catch (KeyNotFoundException ex)
            {
                throw new KeyNotFoundException(
                    $"Cannot find key '{key}' in dictionary <{typeof(TKey).Name}, {typeof(TValue).Name}>", ex);
            }

        }
    }

}